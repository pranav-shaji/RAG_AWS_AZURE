namespace AwsRagChat.Application.Models;

public sealed class DocumentProcessingRequest
{
    public string BucketOrContainerName { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
}
