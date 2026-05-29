namespace AwsRagChat.Infrastructure.Options;

public sealed class BedrockOptions
{
    public const string SectionName = "Bedrock";

    public string EmbeddingModelId { get; set; } = string.Empty;
    public string ChatModelId { get; set; } = string.Empty;
}