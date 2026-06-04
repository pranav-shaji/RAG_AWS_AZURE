using AwsRagChat.Application.Models;

namespace AwsRagChat.Application.Interfaces;

public interface IDocumentProcessor
{
    Task<DocumentProcessingResult> ExtractAsync(
        DocumentProcessingRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentProcessingResult> GetCompletedOcrResultAsync(
        CompletedOcrRequest request,
        CancellationToken cancellationToken = default);
}
