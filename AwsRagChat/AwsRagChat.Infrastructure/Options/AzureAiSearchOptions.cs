namespace AwsRagChat.Infrastructure.Options;

public sealed class AzureAiSearchOptions
{
    public const string SectionName = "VectorStore";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string IndexName { get; set; } = "rag-index";

    public int VectorDimension { get; set; } = 1536; // Default to 1536 (Azure OpenAI text-embedding-3-small / Ada dimensions)
}
