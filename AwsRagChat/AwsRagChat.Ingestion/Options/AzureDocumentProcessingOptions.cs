namespace AwsRagChat.Ingestion.Options;

public sealed class AzureDocumentProcessingOptions
{
    public const string SectionName = "DocumentProcessing";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ModelId { get; set; } = "prebuilt-layout";
}
