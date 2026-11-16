namespace CofindBridge.Configuration;

public sealed class S3Options
{
    public const string SectionName = "S3";

    public string ServiceUrl { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    public bool UseSsl { get; set; } = false;

    public bool ForcePathStyle { get; set; } = true;
}