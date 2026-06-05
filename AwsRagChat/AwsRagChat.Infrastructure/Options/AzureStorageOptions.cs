namespace AwsRagChat.Infrastructure.Options;

public sealed class AzureStorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "Azure";

    public string ConnectionString { get; set; } = string.Empty;

    public string AccountUrl { get; set; } = string.Empty;

    public string BucketOrContainerName { get; set; } = string.Empty;
}
