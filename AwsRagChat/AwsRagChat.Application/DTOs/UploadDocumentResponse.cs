namespace AwsRagChat.Application.DTOs;

public sealed class UploadDocumentResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string S3Key { get; set; } = string.Empty;
    public bool IsDuplicate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ExistingDocumentId { get; set; }
}
