namespace AwsRagChat.Application.Interfaces;

public interface IIngestionPipeline<in TRequest, in TExtractedDocument, TResult>
{
    Task<TResult> ProcessExtractedDocumentAsync(
        TRequest request,
        TExtractedDocument extractedDocument,
        CancellationToken cancellationToken = default);
}
