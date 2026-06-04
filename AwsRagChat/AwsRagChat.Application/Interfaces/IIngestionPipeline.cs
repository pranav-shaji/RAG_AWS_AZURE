namespace AwsRagChat.Application.Interfaces;

public interface IIngestionPipeline<in TRequest, in TExtractedDocument, TResult>
{
    Task<TResult> ProcessExtractedDocumentAsync(
        TRequest request,
        TExtractedDocument extractedDocument,
        Action<string>? log = null,
        CancellationToken cancellationToken = default);
}
