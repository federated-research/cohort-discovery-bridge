using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CofindBridge.Configuration;
using Hutch.Rackit.TaskApi.Models;
using Microsoft.Extensions.Options;

namespace CofindBridge.Services;

public sealed class CohortDiscoveryClient : ICohortDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly BridgeOptions _options;
    private readonly ILogger<CohortDiscoveryClient> _logger;

    public CohortDiscoveryClient(
        HttpClient httpClient,
        IOptions<BridgeOptions> options,
        ILogger<CohortDiscoveryClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.CohortDiscoveryBaseUrl.TrimEnd('/') + "/");
    }

    private BridgeCollectionOptions GetCollection(string collectionId)
    {
        return _options.Collections.FirstOrDefault(
                   c => string.Equals(
                       c.CollectionId,
                       collectionId,
                       StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"No configuration found for collection {collectionId}.");
    }
    
    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string collectionId)
    {
        var collection = GetCollection(collectionId);

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                $"{collection.Username}:{collection.Password}"));

        var request = new HttpRequestMessage(method, path);
        // Currently using Basic Auth
        // Todo: Match the authentication in the Cohort Discovery Tool
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);

        return request;
    }


    public async Task<AvailabilityJob?> FetchPendingTaskAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        var path =
            $"task/nextjob/{Uri.EscapeDataString(collectionId)}";

        _logger.LogInformation(
            "Polling for availability jobs. Collection: {CollectionId}",
            collectionId);

        using var request = CreateRequest(
            HttpMethod.Get,
            path,
            collectionId);

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            _logger.LogInformation(
                "Task API returned no pending jobs for collection {CollectionId}",
                collectionId);

            return null;
        }

        _logger.LogInformation(
            "Task API returned HTTP {StatusCode} for collection {CollectionId}",
            (int)response.StatusCode,
            collectionId);

        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            _logger.LogWarning(
                "Task API returned an empty response for collection {CollectionId}",
                collectionId);

            return null;
        }

        _logger.LogDebug(
            "Received job payload from collection {CollectionId}: {Payload}",
            collectionId,
            responseBody);

        var job = JsonSerializer.Deserialize<AvailabilityJob>(
            responseBody,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (job is null)
        {
            _logger.LogWarning(
                "Could not deserialize availability job for collection {CollectionId}",
                collectionId);

            return null;
        }

        _logger.LogInformation(
            "Successfully received job {TaskId} from collection {CollectionId}",
            job.Uuid,
            collectionId);

        return job;
    }


    public async Task SubmitResultAsync(
        string taskId,
        string collectionId,
        JobResult result,
        CancellationToken cancellationToken)
    {
        var path =
            $"task/result/{Uri.EscapeDataString(taskId)}/" +
            $"{Uri.EscapeDataString(collectionId)}";

        using var request = CreateRequest(
            HttpMethod.Post,
            path,
            collectionId);

        request.Content = new StringContent(
            JsonSerializer.Serialize(result),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "Result submitted for task {TaskId}, collection {CollectionId}",
            taskId,
            collectionId);
    }
}