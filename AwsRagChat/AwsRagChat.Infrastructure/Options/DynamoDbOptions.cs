namespace AwsRagChat.Infrastructure.Options;

public sealed class DynamoDbOptions
{
    public const string SectionName = "DynamoDb";

    public string TableName { get; set; } = string.Empty;
}