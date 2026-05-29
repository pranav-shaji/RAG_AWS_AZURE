using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.Interfaces;

public interface IChunkRepository
{
    Task SaveChunksAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentChunk>> GetChunksByDocumentIdAsync(
        string ownerUserId,
        string documentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentChunk>> GetChunksByDocumentsAsync(
        IReadOnlyList<string> documentIds,
        CancellationToken cancellationToken = default,
        int? maxChunks = null);
}
