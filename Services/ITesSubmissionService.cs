using System.Text.Json;

namespace CofindBridge.Services;

public interface ITesSubmissionService
{
    Task<string> SubmitAvailabilityTaskAsync(
        string jobUuid,
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