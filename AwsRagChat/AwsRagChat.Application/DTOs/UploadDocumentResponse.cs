namespace AwsRagChat.Application.DTOs;

public sealed class UploadDocumentResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;

    [System.Obsolete("Use StorageKey instead.")]
    public string S3Key
    {
        get => StorageKey;
        set => StorageKey = value;
    }
    public bool IsDuplicate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ExistingDocumentId { get; set; }
}
