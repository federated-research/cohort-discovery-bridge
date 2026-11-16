using CofindBridge.Configuration;
using CofindBridge.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BridgeOptions>()
    .Bind(builder.Configuration.GetSection(BridgeOptions.SectionName))
    .Validate(
        options => options.Collections.Count > 0,
        "At least one collection must be configured.")
    .Validate(
        options => options.Collections.All(c =>
            !string.IsNullOrWhiteSpace(c.CollectionId) &&
            !string.IsNullOrWhiteSpace(c.Username) &&
            !string.IsNullOrWhiteSpace(c.Password)),
        "Every collection must have a collection ID, username and password.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(
            options.CohortDiscoveryBaseUrl),
        "CohortDiscoveryBaseUrl must be configured.")
    .ValidateOnStart();

builder.Services.Configure<TesOptions>(
    builder.Configuration.GetSection(TesOptions.SectionName));

builder.Services.AddHttpClient<
    ITesSubmissionService,
    TesSubmissionService>();

builder.Services.AddHttpClient<
    ICohortDiscoveryClient,
    CohortDiscoveryClient>();

builder.Services.Configure<S3Options>(
    builder.Configuration.GetSection(S3Options.SectionName));
builder.Services.AddSingleton<IS3Store, S3Store>();
builder.Services.AddHostedService<CohortDiscoveryPollingWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Cohort Discovery Bridge",
    status = "Running"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy"
}));

app.Run();