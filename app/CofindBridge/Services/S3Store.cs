using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CofindBridge.Configuration;
using Microsoft.Extensions.Options;

namespace CofindBridge.Services;

public sealed class S3Store : IS3Store, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly ILogger<S3Store> _logger;

    public S3Store(
        IOptions<S3Options> options,
        ILogger<S3Store> logger)
    {
        var settings = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            throw new InvalidOperationException(
                "S3:ServiceUrl must be configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.AccessKey) ||
            string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            throw new InvalidOperationException(
                "S3 access and secret keys must be configured.");
        }

        var config = new AmazonS3Config
        {
            ServiceURL = settings.ServiceUrl,
            ForcePathStyle = settings.ForcePathStyle,
            AuthenticationRegion = settings.Region,
            UseHttp = !settings.UseSsl
        };

        var credentials = new BasicAWSCredentials(
            settings.AccessKey,
            settings.SecretKey);

        _client = new AmazonS3Client(credentials, config);
    }

    public async Task<string> GetFromStoreAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Downloading S3 object {ObjectKey} from bucket {Bucket}",
            objectKey,
            bucket);

        using var response = await _client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = bucket,
                Key = objectKey
            },
            cancellationToken);

        using var reader = new StreamReader(response.ResponseStream);

        var contents = await reader.ReadToEndAsync(cancellationToken);

        _logger.LogInformation(
            "Downloaded S3 object {ObjectKey} from bucket {Bucket}",
            objectKey,
            bucket);

        return contents;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}