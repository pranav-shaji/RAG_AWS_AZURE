namespace AwsRagChat.Ingestion.Options;

public sealed class TextractAsyncOptions
{
    public const string SectionName = "TextractAsync";

    public string SnsTopicArn { get; set; } = string.Empty;
    public string TextractPublishRoleArn { get; set; } = string.Empty;
    public string JobTag { get; set; } = "aws-rag-chat-ocr";
}