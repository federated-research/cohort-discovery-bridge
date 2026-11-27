using System.Text.Json;
using CofindBridge.Configuration;
using Hutch.Rackit.TaskApi.Models;
using Microsoft.Extensions.Options;

namespace CofindBridge.Services;

public sealed class CohortDiscoveryPollingWorker : BackgroundService
{
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
            collection => PollCollectionAsync(collection, stoppingToken));

        await Task.WhenAll(pollingTasks);
    }

    private async Task PollCollectionAsync(
        BridgeCollectionOptions collection,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Polling for Collection: {CollectionId}",
            collection.CollectionId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var distributionJob = await _client.FetchPendingDistributionJobAsync(
                    collection.CollectionId,
                    stoppingToken);

                if (distributionJob is not null)
                {
                    await ProcessDistributionJobAsync(
                        collection,
                        distributionJob,
                        stoppingToken);
                }

                var availabilityJob = await _client.FetchPendingAvailabilityJobAsync(
                    collection.CollectionId,
                    stoppingToken);

                if (availabilityJob is not null)
                {
                    await ProcessAvailabilityJobAsync(
                        collection,
                        availabilityJob,
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
                    collection.CollectionId);
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
            collection.CollectionId);
    }

    private async Task ProcessAvailabilityJobAsync(
        BridgeCollectionOptions collection,
        AvailabilityJob job,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing availability job {Uuid} for collection {CollectionId}",
            job.Uuid,
            collection.CollectionId);

        // Submit the availability job to 5STES.
        var tesTaskId = await _tesSubmissionService.SubmitAvailabilityTaskAsync(
            job,
            _options.Project,
            collection.TreName,
            cancellationToken);

        _logger.LogInformation(
            "Availability job {Uuid} submitted to 5STES as submission {SubmissionId}",
            job.Uuid,
            tesTaskId);
        // Get the child submission ID
        var childSubmissionId = await _tesSubmissionService.GetChildSubmissionIdAsync(
            int.Parse(tesTaskId),
            collection.TreName,
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

        // Create the result for the upstream job.
        var jobResult = new JobResult
        {
            Status = "ok",
            ProtocolVersion = job.ProtocolVersion,
            CollectionId = collection.CollectionId,
            Uuid = job.Uuid,
            Results = result
        };

        // Submit the result back to the upstream API.
        await _client.SubmitResultAsync(
            job.Uuid,
            collection.CollectionId,
            ".a",
            jobResult,
            cancellationToken);

        _logger.LogInformation(
            "Successfully submitted result for availability job {Uuid} to collection {CollectionId}",
            job.Uuid,
            collection.CollectionId);
    }

    private async Task ProcessDistributionJobAsync(
        BridgeCollectionOptions collection,
        CollectionAnalysisJob job,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing distribution job {Uuid} for collection {CollectionId}",
            job.Uuid,
            collection.CollectionId);

        var tesTaskId = await _tesSubmissionService.SubmitDistributionTaskAsync(
            job,
            _options.Project,
            collection.TreName,
            cancellationToken);

        var childSubmissionId = await _tesSubmissionService.GetChildSubmissionIdAsync(
            int.Parse(tesTaskId),
            collection.TreName,
            cancellationToken);

        using var submission = await _tesSubmissionService.WaitForCompletionAsync(
            childSubmissionId,
            cancellationToken);

        var outputBucket = submission.RootElement
            .GetProperty("project")
            .GetProperty("outputBucket")
            .GetString();

        if (string.IsNullOrWhiteSpace(outputBucket))
        {
            throw new InvalidOperationException(
                $"No output bucket found for TES submission {childSubmissionId}.");
        }

        using var output = await _tesSubmissionService.DownloadResultsFromS3Async(
            childSubmissionId.ToString(),
            outputBucket,
            cancellationToken);

        var outputResult = ParseDistributionOutput(output, childSubmissionId);

        var jobResult = new JobResult
        {
            Status = outputResult.Status,
            CollectionId = collection.CollectionId,
            Uuid = job.Uuid,
            Results = outputResult.Results,
            Message = outputResult.Message
        };

        await _client.SubmitResultAsync(
            job.Uuid,
            collection.CollectionId,
            ".b",
            jobResult,
            cancellationToken);

        _logger.LogInformation(
            "Successfully submitted result for distribution job {Uuid} to collection {CollectionId}",
            job.Uuid,
            collection.CollectionId);
    }

    private static JobResult ParseDistributionOutput(
        JsonDocument output,
        int childSubmissionId)
    {
        if (!output.RootElement.TryGetProperty("queryResult", out _))
        {
            throw new InvalidOperationException(
                $"TES output for submission {childSubmissionId} does not contain queryResult.");
        }

        var outputResult = JsonSerializer.Deserialize<JobResult>(
                               output.RootElement.GetRawText(),
                               new JsonSerializerOptions(JsonSerializerDefaults.Web))
                           ?? throw new InvalidOperationException(
                               $"Could not deserialize distribution output for TES submission {childSubmissionId}.");

        foreach (var file in outputResult.Results.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileData))
            {
                throw new InvalidOperationException(
                    $"Distribution output for TES submission {childSubmissionId} " +
                    $"contains an empty file named '{file.FileName}'.");
            }

            file.WithData(file.FileData);
        }

        return outputResult;
    }
}
