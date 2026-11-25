using System.Text.Json;
using CofindBridge.Configuration;
using Hutch.Rackit.TaskApi.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace CofindBridge.Services;

public class TesSubmissionService : ITesSubmissionService
{
    private readonly HttpClient _httpClient;
    private readonly TesOptions _options;
    private readonly ILogger<TesSubmissionService> _logger;
    private readonly IS3Store _s3Store;

    public TesSubmissionService(
        HttpClient httpClient,
        IOptions<TesOptions> options,
        ILogger<TesSubmissionService> logger, IS3Store s3Store)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _s3Store = s3Store;
        _httpClient.BaseAddress = new Uri(
            _options.BaseUrl.TrimEnd('/') + "/");
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                "The TES API token has not been configured.");
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.Token);
    }

    public async Task<string> SubmitAvailabilityTaskAsync(
        AvailabilityJob job,
        string project,
        string treName,
        CancellationToken cancellationToken)
    {
        // Maps directly to bunny-cli's AvailabilityQuery model
        var availabilityQuery = new
        {
            cohort = job.Cohort,
            uuid = job.Uuid,
            owner = job.Owner,
            collection = job.Collection,
            protocol_version = job.ProtocolVersion,
            char_salt = job.CharSalt
        };

        var cohortJson = JsonSerializer.Serialize(availabilityQuery);

        var tesTask = new
        {
            state = 0,
            name = "Bunny Availability",
            description = (string?)null,
            inputs = (object?)null,
            outputs = new[]
            {
                new
                {
                    name = "Query Results",
                    description =
                        "Results from the requested availability query execution",
                    url = "s3://",
                    path = "/outputs",
                    type = "DIRECTORY"
                }
            },
            resources = (object?)null,
            executors = new[]
            {
                new
                {
                    image =
                        "ghcr.io/health-informatics-uon/five-safes-tes-analytics-bunny-cli:1.6.0",
                    command = new[]
                    {
                        "--body-json",
                        cohortJson,
                        "--output",
                        "/outputs/output.json",
                        "--no-encode"
                    },
                    workdir = (string?)null,
                    stdin = (string?)null,
                    stdout = (string?)null,
                    stderr = (string?)null,
                    env = new Dictionary<string, string>()
                }
            },
            volumes = (object?)null,
            tags = new Dictionary<string, string>
            {
                ["Project"] = project,
                ["tres"] = treName
            },
            logs = (object?)null,
            creation_time = (string?)null
        };

        var path = _options.SubmitPath.TrimStart('/');

        _logger.LogInformation(
            "Submitting TES availability task to {BaseUrl}/{Path}",
            _options.BaseUrl,
            path);

        using var response = await _httpClient.PostAsJsonAsync(
            path,
            tesTask,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "TES submission failed with {StatusCode}: {Body}",
                (int)response.StatusCode,
                responseBody);

            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(responseBody);

        if (!document.RootElement.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new InvalidOperationException(
                $"TES submission succeeded but no task ID was found " +
                $"in the response: {responseBody}");
        }

        var taskId = idElement.GetString()!;

        _logger.LogInformation(
            "TES task submitted successfully. Task ID: {TaskId}",
            taskId);

        return taskId;
    }

    public async Task<JsonDocument> WaitForCompletionAsync(
        int submissionId,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime <
               TimeSpan.FromSeconds(_options.TimeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _httpClient.GetAsync(
                $"api/Submission/GetASubmission/{submissionId}?responseType=summary",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            using var document = JsonDocument.Parse(json);

            var status = document.RootElement
                .GetProperty("status")
                .GetInt32();

            switch (status)
            {
                case 11: // Completed
                    _logger.LogInformation(
                        "TES submission {SubmissionId} completed.",
                        submissionId);

                    return JsonDocument.Parse(json);

                case 27: // Failed
                case 25: // PodProcessingFailed
                    throw new InvalidOperationException(
                        $"TES submission {submissionId} failed with status {status}.");

                case 16: // Cancelled
                case 6: // DataOutApprovalRejected
                    throw new InvalidOperationException(
                        $"TES submission {submissionId} was cancelled or rejected. Status: {status}");

                default:
                    _logger.LogDebug(
                        "TES submission {SubmissionId} is still processing. Status: {Status}",
                        submissionId,
                        status);
                    break;
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_options.PollIntervalSeconds),
                cancellationToken);
        }

        throw new TimeoutException(
            $"TES submission {submissionId} did not complete within {_options.TimeoutSeconds} seconds.");
    }


    public async Task<JsonDocument> DownloadResultsFromS3Async(
        string submissionId,
        string outputBucket,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(submissionId, out var id))
        {
            throw new InvalidOperationException(
                $"Invalid Submission Layer ID: {submissionId}");
        }


        var objectKey = $"{submissionId}/output.json";

        _logger.LogInformation(
            "Retrieving TES output for submission {SubmissionId} from S3",
            submissionId);

        var contents = await _s3Store.GetFromStoreAsync(
            outputBucket,
            objectKey,
            cancellationToken);

        _logger.LogInformation(
            "Successfully retrieved TES output for submission {SubmissionId}",
            submissionId);

        return JsonDocument.Parse(contents);
    }

    public async Task<int> GetChildSubmissionIdAsync(
        int parentSubmissionId,
        string treName,
        CancellationToken cancellationToken)
    {
        var url =
            $"api/Submission/GetChildSubmissionInfoByParentAndTre" +
            $"?parentSubmissionId={parentSubmissionId}" +
            $"&treName={Uri.EscapeDataString(treName)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        return json.RootElement.GetProperty("id").GetInt32();
    }
}