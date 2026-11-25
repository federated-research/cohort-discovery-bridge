using System.Text.Json;
using CofindBridge.Configuration;
using Hutch.Rackit.TaskApi.Models;
using Microsoft.Extensions.Options;

namespace CofindBridge.Services;

public sealed class CohortDiscoveryPollingWorker : BackgroundService
{
    // Valid Rule.Category (varcat) values per bunny-cli's Varcat enum
    private static readonly HashSet<string> ValidVarcats = new(StringComparer.Ordinal)
    {
        "Person", "Condition", "Observation", "Drug", "Measurement",
        "Medication", "Procedure", "Specimen", "Location", "Death"
    };

    private readonly ICohortDiscoveryClient _client;
    private readonly BridgeOptions _options;
    private readonly ILogger<CohortDiscoveryPollingWorker> _logger;
    private readonly ITesSubmissionService _tesSubmissionService;

    public CohortDiscoveryPollingWorker(
        ICohortDiscoveryClient client,
        IOptions<BridgeOptions> options,
        ILogger<CohortDiscoveryPollingWorker> logger,
        ITesSubmissionService tesSubmissionService)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _tesSubmissionService = tesSubmissionService;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (_options.Collections.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one collection must be configured.");
        }

        _logger.LogInformation(
            "Cohort Discovery Bridge started with {CollectionCount} collections",
            _options.Collections.Count);

        var pollingTasks = _options.Collections.Select(
            collection => PollCollectionAsync(
                collection.CollectionId,
                stoppingToken));

        await Task.WhenAll(pollingTasks);
    }

    private async Task PollCollectionAsync(
        string collectionId,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Polling for Collection: {CollectionId}",
            collectionId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _client.FetchPendingTaskAsync(
                    collectionId,
                    stoppingToken);

                if (job is not null)
                {
                    await ProcessJobAsync(
                        collectionId,
                        job,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error polling or processing collection {CollectionId}",
                    collectionId);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        Math.Max(1, _options.PollIntervalSeconds)),
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation(
            "Polling stopped for collection {CollectionId}",
            collectionId);
    }

    private async Task ProcessJobAsync(
        string collectionId,
        AvailabilityJob job,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing availability job {Uuid} for collection {CollectionId}",
            job.Uuid,
            collectionId);

        ValidateJob(job);

        // Submit the availability task to TES API.
        var tesTaskId = await _tesSubmissionService.SubmitAvailabilityTaskAsync(job, _options.Project,
            _options.TreName, cancellationToken);

        _logger.LogInformation(
            "Availability job {Uuid} submitted to TES API as task {TaskId}",
            job.Uuid,
            tesTaskId);
        // Get the child submission ID
        var childSubmissionId = await _tesSubmissionService.GetChildSubmissionIdAsync(
            int.Parse(tesTaskId),
            _options.TreName,
            cancellationToken);

        // Wait for the submission to complete.
        using var submission = await _tesSubmissionService.WaitForCompletionAsync(
            childSubmissionId,
            cancellationToken);

        // Get the output bucket from the submission summary.
        var outputBucket = submission.RootElement
            .GetProperty("project")
            .GetProperty("outputBucket")
            .GetString();

        if (string.IsNullOrWhiteSpace(outputBucket))
        {
            throw new InvalidOperationException(
                $"No output bucket found for TES submission {childSubmissionId}.");
        }

        _logger.LogInformation(
            "Downloading results for TES submission {TaskId} from bucket {Bucket}",
            childSubmissionId,
            outputBucket);

        // Download output.json from S3 bucket.
        using var output = await _tesSubmissionService.DownloadResultsFromS3Async(
            childSubmissionId.ToString(),
            outputBucket,
            cancellationToken);

        // Extract the query result from output.json.
        if (!output.RootElement.TryGetProperty("queryResult", out var queryResult))
        {
            throw new InvalidOperationException(
                $"TES output for submission {childSubmissionId} does not contain queryResult.");
        }

        var result = JsonSerializer.Deserialize<QueryResult>(
                         queryResult.GetRawText(),
                         new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException(
                         $"Could not deserialize queryResult for TES submission {childSubmissionId}.");

        // Create the JobResult
        var jobResult = new JobResult
        {
            Status = "ok",
            ProtocolVersion = job.ProtocolVersion,
            CollectionId = collectionId,
            Uuid = job.Uuid,
            Results = result
        };

        // Submit the result back to the Task API.
        await _client.SubmitResultAsync(
            job.Uuid,
            collectionId,
            jobResult,
            cancellationToken);

        _logger.LogInformation(
            "Successfully submitted result for availability job {Uuid} to collection {CollectionId}",
            job.Uuid,
            collectionId);
    }

    // Validates the fields bunny-cli's AvailabilityQuery models require, before building a TES submission from them.
    private static void ValidateJob(AvailabilityJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Uuid))
        {
            throw new InvalidOperationException(
                "Availability job is missing a uuid.");
        }

        if (string.IsNullOrWhiteSpace(job.Collection))
        {
            throw new InvalidOperationException(
                $"Availability job {job.Uuid} is missing a collection.");
        }

        if (string.IsNullOrWhiteSpace(job.ProtocolVersion))
        {
            throw new InvalidOperationException(
                $"Availability job {job.Uuid} is missing a protocol_version.");
        }

        if (string.IsNullOrWhiteSpace(job.CharSalt))
        {
            throw new InvalidOperationException(
                $"Availability job {job.Uuid} is missing a char_salt.");
        }

        if (job.Cohort.Groups.Count == 0)
        {
            throw new InvalidOperationException(
                $"Availability job {job.Uuid} has a cohort with no groups.");
        }

        foreach (var group in job.Cohort.Groups)
        {
            if (group.Rules.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Availability job {job.Uuid} has a cohort group with no rules.");
            }

            foreach (var rule in group.Rules)
            {
                if (!ValidVarcats.Contains(rule.Category))
                {
                    throw new InvalidOperationException(
                        $"Availability job {job.Uuid} has a rule with invalid " +
                        $"varcat '{rule.Category}'. Must be one of: " +
                        $"{string.Join(", ", ValidVarcats)}.");
                }
            }
        }
    }
}