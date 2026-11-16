using Hutch.Rackit.TaskApi.Models;

namespace CofindBridge.Services;

public interface ICohortDiscoveryClient
{
    Task<AvailabilityJob?> FetchPendingTaskAsync(
        string collectionId,
        CancellationToken cancellationToken);

    Task SubmitResultAsync(
        string taskId,
        string collectionId,
        JobResult result,
        CancellationToken cancellationToken);
}