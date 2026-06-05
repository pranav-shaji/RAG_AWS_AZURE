namespace AwsRagChat.Infrastructure.Options;

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAi";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string EmbeddingDeploymentName { get; set; } = string.Empty;

    public string ChatDeploymentName { get; set; } = string.Empty;
}
