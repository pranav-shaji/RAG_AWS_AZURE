using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.Interfaces;

public interface IVectorStore : IVectorSearchService
{
    Task IndexDocumentAsync(DocumentChunk chunk);
}
