using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.Interfaces;

public interface IVectorSearchService
{
    Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerUserId,
        string? documentId,
        IReadOnlyList<float> queryEmbedding,
        int topK = 5,
        bool searchSharedAdminDocuments = false,
        IReadOnlyList<string>? sharedDocumentIds = null,
        string? currentUserRole = null,
        CancellationToken cancellationToken = default);
}
