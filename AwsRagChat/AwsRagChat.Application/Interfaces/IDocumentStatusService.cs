namespace AwsRagChat.Application.Interfaces;

public interface IDocumentStatusService
{
    Task MarkUploadedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        CancellationToken cancellationToken = default);

    Task MarkProcessingAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        CancellationToken cancellationToken = default);

    Task MarkOcrStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        string textractJobId,
        CancellationToken cancellationToken = default);

    Task MarkOcrCompletedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        string textractJobId,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default);

    Task MarkEmbeddingStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        CancellationToken cancellationToken = default);

    Task MarkIndexingStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        CancellationToken cancellationToken = default);

    Task MarkIndexedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string s3Key,
        string errorMessage,
        CancellationToken cancellationToken = default);

    Task<bool> GetIsAdminDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    Task<List<string>> GetAllowedRolesAsync(
        string documentId,
        CancellationToken cancellationToken = default);
}
