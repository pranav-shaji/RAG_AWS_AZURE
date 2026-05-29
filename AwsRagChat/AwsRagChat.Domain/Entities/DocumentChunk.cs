namespace AwsRagChat.Domain.Entities;

public sealed class DocumentChunk
{
    public string OwnerUserId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string S3Key { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Heading { get; set; } = string.Empty;
    public int ChunkOrder { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<float> Embedding { get; set; } = [];
    public bool IsAdminDocument { get; set; }
    public List<string> AllowedRoles { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
