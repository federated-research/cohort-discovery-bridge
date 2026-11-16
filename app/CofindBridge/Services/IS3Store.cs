namespace CofindBridge.Services;

public interface IS3Store
{
    Task<string> GetFromStoreAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken);
}