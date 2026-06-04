using Amazon.S3;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;

namespace AwsRagChat.Ingestion.Aws;

public sealed class AwsIngestionServices
{
    public required IAmazonS3 AmazonS3 { get; init; }

    public required TextExtractionService TextExtractionService { get; init; }

    public required TextractTextExtractionService TextractTextExtractionService { get; init; }

    public required TextractAsyncExtractionService TextractAsyncExtractionService { get; init; }

    public required IDocumentStatusService DocumentStatusService { get; init; }

    public required IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult>
        DocumentIngestionPipeline { get; init; }
}
