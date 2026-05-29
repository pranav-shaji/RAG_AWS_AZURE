namespace AwsRagChat.Infrastructure.Options;

public sealed class ConversationStorageOptions
{
    public const string SectionName = "Conversations";

    public string TableName { get; set; } = string.Empty;
}