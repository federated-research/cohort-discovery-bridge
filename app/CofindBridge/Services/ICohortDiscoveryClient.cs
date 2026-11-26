using Hutch.Rackit.TaskApi.Models;

namespace CofindBridge.Services;

public interface ICohortDiscoveryClient
{
    Task<AvailabilityJob?> FetchPendingAvailabilityJobAsync(
        string collectionId,
        CancellationToken cancellationToken);

    Task<CollectionAnalysisJob?> FetchPendingDistributionJobAsync(
        string collectionId,
        CancellationToken cancellationToken);

    Task SubmitResultAsync(
        string jobId,
        string collectionId,
        string queueSuffix,
        JobResult result,
        CancellationToken cancellationToken);
}
