namespace AwsRagChat.Application.Models;

public sealed class StorageObjectReadResult
{
    public required Stream Content { get; init; }

    public string? ContentType { get; init; }

    public long? ContentLength { get; init; }
}
