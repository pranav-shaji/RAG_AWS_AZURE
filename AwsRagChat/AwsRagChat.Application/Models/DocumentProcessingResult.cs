namespace AwsRagChat.Application.Models;

public sealed class DocumentProcessingResult
{
    public DocumentProcessingStatus Status { get; init; }

    public ExtractedDocument? ExtractedDocument { get; init; }

    public string? OcrJobId { get; init; }

    public string? Message { get; init; }
}

public enum DocumentProcessingStatus
{
    Extracted,
    OcrStarted,
    Unsupported,
    Failed
}
