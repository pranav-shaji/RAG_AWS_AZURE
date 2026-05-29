using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.DTOs;

public sealed class CreateConversationRequest
{
    public string? Title { get; set; }
}

public sealed class ConversationSessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime LastMessageAtUtc { get; set; }
    public int MessageCount { get; set; }
    public bool IsArchived { get; set; }
}

public sealed class ConversationMessageDto
{
    public string MessageId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int TokensApprox { get; set; }
    public List<Citation> Citations { get; set; } = [];
    public string ResponseType { get; set; } = "text";
    public object? Data { get; set; }
    public ChartData? ChartData { get; set; }
}
