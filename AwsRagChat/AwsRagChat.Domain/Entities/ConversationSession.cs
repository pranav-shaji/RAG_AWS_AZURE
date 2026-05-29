namespace AwsRagChat.Domain.Entities;

public sealed class ConversationSession
{
    public string SessionId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string Title { get; set; } = "New chat";
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime LastMessageAtUtc { get; set; }
    public int MessageCount { get; set; }
    public bool IsArchived { get; set; }
}