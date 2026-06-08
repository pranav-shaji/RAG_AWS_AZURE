namespace AwsRagChat.Infrastructure.Options;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    public string Endpoint { get; set; } = string.Empty;

    public string AuthKey { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "AwsRagChatDb";

    public string DocumentsContainer { get; set; } = "Documents";

    public string ChunksContainer { get; set; } = "Chunks";

    public string ConversationsContainer { get; set; } = "Conversations";

    public string UsersContainer { get; set; } = "Users";
}
