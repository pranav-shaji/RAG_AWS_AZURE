namespace AwsRagChat.Application.Models;

public sealed class StorageObjectReadRequest
{
    public string BucketOrContainerName { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;
}
