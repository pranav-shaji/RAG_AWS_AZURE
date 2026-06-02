namespace AwsRagChat.Ingestion.Models;

public sealed class IngestionPipelineResult
{
    public bool Succeeded { get; init; }
    public int ChunkCount { get; init; }
    public int PageCount { get; init; }
    public string? ErrorMessage { get; init; }
}
