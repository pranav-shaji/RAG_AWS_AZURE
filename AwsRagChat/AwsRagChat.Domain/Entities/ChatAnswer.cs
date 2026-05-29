namespace AwsRagChat.Domain.Entities;

public sealed class ChatAnswer
{
    public string Answer { get; set; } = string.Empty;
    public List<Citation> Citations { get; set; } = [];
}