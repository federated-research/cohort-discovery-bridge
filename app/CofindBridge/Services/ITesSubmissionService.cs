using System.Text.Json;
using Hutch.Rackit.TaskApi.Models;

namespace CofindBridge.Services;

public interface ITesSubmissionService
{
    Task<string> SubmitAvailabilityTaskAsync(
        AvailabilityJob availabilityJob,
        string project,
        string treName,
        CancellationToken cancellationToken);

    Task<string> SubmitDistributionTaskAsync(
        CollectionAnalysisJob job,
        string project,
        string treName,
        CancellationToken cancellationToken);

    Task<JsonDocument> WaitForCompletionAsync(
        int childSubmissionId,
        CancellationToken cancellationToken);

    Task<JsonDocument> DownloadResultsFromS3Async(
        string childSubmissionId,
        string outputBucket,
        CancellationToken cancellationToken);

    Task<int> GetChildSubmissionIdAsync(
        int parentSubmissionId,
        string treName,
        CancellationToken cancellationToken);
}