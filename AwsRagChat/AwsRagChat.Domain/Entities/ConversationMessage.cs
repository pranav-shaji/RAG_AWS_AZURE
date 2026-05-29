namespace AwsRagChat.Domain.Entities;

public sealed class ConversationMessage
{
    public string SessionId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int TokensApprox { get; set; }
    public List<Citation> Citations { get; set; } = [];
    public string ResponseType { get; set; } = "text";
    public string DataJson { get; set; } = string.Empty;
    public string ChartDataJson { get; set; } = string.Empty;
}
