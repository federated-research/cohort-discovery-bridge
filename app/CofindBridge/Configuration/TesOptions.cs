namespace CofindBridge.Configuration;

public sealed class TesOptions
{
    public const string SectionName = "Tes";

    public string BaseUrl { get; set; } = "http://localhost:5034";

    public string SubmitPath { get; set; } = "/v1/tasks";

    public string Token { get; set; } = "";

    public int PollIntervalSeconds { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 480;
}