namespace AwsRagChat.Domain.Entities;

public sealed class Citation
{
    public string DocumentId { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string Snippet { get; set; } = string.Empty;
}