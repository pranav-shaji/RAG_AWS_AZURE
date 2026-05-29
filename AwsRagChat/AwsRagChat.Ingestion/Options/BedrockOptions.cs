namespace AwsRagChat.Ingestion.Options;

public sealed class BedrockOptions
{
    public const string SectionName = "Bedrock";

    public string EmbeddingModelId { get; set; } = string.Empty;
}