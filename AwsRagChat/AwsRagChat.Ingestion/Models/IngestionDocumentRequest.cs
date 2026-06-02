namespace AwsRagChat.Ingestion.Models;

public sealed class IngestionDocumentRequest
{
    public string DocumentId { get; init; } = string.Empty;
    public string OwnerUserId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ObjectKey { get; init; } = string.Empty;
    public string OcrJobId { get; init; } = string.Empty;
    public string EmptyTextErrorMessage { get; init; } = "No extractable text found.";
}
