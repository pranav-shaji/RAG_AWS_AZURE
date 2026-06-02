namespace AwsRagChat.Infrastructure.Common;

public sealed class CloudProviderOptions
{
    public string CloudProvider { get; init; } = CloudProviderNames.Aws;
}
