namespace CofindBridge.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public List<BridgeCollectionOptions> Collections { get; set; } = new();

    public int PollIntervalSeconds { get; set; } = 20;

    public string CohortDiscoveryBaseUrl { get; set; } = string.Empty;
    
    public string TreName { get; set; } = string.Empty;
}