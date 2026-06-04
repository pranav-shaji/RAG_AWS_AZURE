namespace AwsRagChat.Application.Models;

public sealed class CompletedOcrRequest
{
    public string OcrJobId { get; init; } = string.Empty;

    public string BucketOrContainerName { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
}
