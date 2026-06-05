using AwsRagChat.Application.Interfaces;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;

namespace AwsRagChat.Ingestion.Aws;

public sealed class AwsIngestionServices
{
    public required IDocumentProcessor DocumentProcessor { get; init; }

    public required IDocumentStatusService DocumentStatusService { get; init; }

    public required IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult>
        DocumentIngestionPipeline { get; init; }
}
