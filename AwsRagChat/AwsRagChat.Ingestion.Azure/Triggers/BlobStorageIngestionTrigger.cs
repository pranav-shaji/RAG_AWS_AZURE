using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
using ExtractedPage = AwsRagChat.Ingestion.Services.ExtractedPage;

namespace AwsRagChat.Ingestion.Azure.Triggers;

public class BlobStorageIngestionTrigger
{
    private readonly IDocumentProcessor _documentProcessor;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;
    private readonly ILogger<BlobStorageIngestionTrigger> _logger;

    public BlobStorageIngestionTrigger(
        IDocumentProcessor documentProcessor,
        IDocumentStatusService documentStatusService,
        IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> documentIngestionPipeline,
        ILogger<BlobStorageIngestionTrigger> logger)
    {
        _documentProcessor = documentProcessor;
        _documentStatusService = documentStatusService;
        _documentIngestionPipeline = documentIngestionPipeline;
        _logger = logger;
    }

    [Function("BlobStorageIngestionTrigger")]
    public async Task<IngestionTriggerResult?> Run(
        [BlobTrigger("uploads/{userId}/{documentId}/{fileName}", Connection = "AzureWebJobsStorage")] Stream blobStream,
        string userId,
        string documentId,
        string fileName,
        FunctionContext context)
    {
        _logger.LogInformation("Processing Blob trigger. Container: uploads, UserId: {userId}, DocumentId: {documentId}, FileName: {fileName}", userId, documentId, fileName);

        var objectKey = $"uploads/{userId}/{documentId}/{fileName}";

        try
        {
            await _documentStatusService.MarkUploadedAsync(
                documentId,
                userId,
                fileName,
                objectKey,
                CancellationToken.None);

            await _documentStatusService.MarkProcessingAsync(
                documentId,
                userId,
                fileName,
                objectKey,
                CancellationToken.None);

            var processingResult = await _documentProcessor.ExtractAsync(
                new DocumentProcessingRequest
                {
                    BucketOrContainerName = "uploads", // Since it is the trigger container
                    ObjectKey = objectKey,
                    FileName = fileName
                },
                CancellationToken.None);

            if (processingResult.Status == DocumentProcessingStatus.OcrStarted)
            {
                _logger.LogInformation("Fallback to Azure Document Intelligence async OCR. Started job: {jobId}", processingResult.OcrJobId);

                await _documentStatusService.MarkOcrStartedAsync(
                    documentId,
                    userId,
                    fileName,
                    objectKey,
                    processingResult.OcrJobId!,
                    CancellationToken.None);

                var ocrMessage = new OcrCompletionMessage
                {
                    DocumentId = documentId,
                    OwnerUserId = userId,
                    FileName = fileName,
                    ObjectKey = objectKey,
                    OcrJobId = processingResult.OcrJobId!
                };

                return new IngestionTriggerResult
                {
                    QueueMessage = JsonSerializer.Serialize(ocrMessage)
                };
            }

            if (processingResult.Status == DocumentProcessingStatus.Unsupported)
            {
                throw new NotSupportedException(processingResult.Message ?? $"File type '{Path.GetExtension(fileName)}' is not supported.");
            }

            if (processingResult.Status == DocumentProcessingStatus.Failed)
            {
                throw new InvalidOperationException(processingResult.Message ?? "Document extraction failed.");
            }

            if (processingResult.ExtractedDocument == null)
            {
                throw new InvalidOperationException("No extracted document content was returned.");
            }

            var extracted = new AwsRagChat.Ingestion.Services.ExtractedDocument
            {
                FullText = processingResult.ExtractedDocument.FullText,
                PageCount = processingResult.ExtractedDocument.PageCount,
                Pages = processingResult.ExtractedDocument.Pages
                    .Select(p => new ExtractedPage
                    {
                        PageNumber = p.PageNumber,
                        Text = p.Text
                    })
                    .ToList()
            };

            await _documentIngestionPipeline.ProcessExtractedDocumentAsync(
                new IngestionDocumentRequest
                {
                    DocumentId = documentId,
                    OwnerUserId = userId,
                    FileName = fileName,
                    ObjectKey = objectKey,
                    EmptyTextErrorMessage = "No extractable text found."
                },
                extracted,
                msg => _logger.LogInformation("{msg}", msg),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ERROR during document ingestion: {message}", ex.Message);

            await _documentStatusService.MarkFailedAsync(
                documentId,
                userId,
                fileName,
                objectKey,
                ex.Message,
                CancellationToken.None);
        }

        return null;
    }
}

public class IngestionTriggerResult
{
    [QueueOutput("ocr-completion-queue", Connection = "AzureWebJobsStorage")]
    public string? QueueMessage { get; set; }
}

public class OcrCompletionMessage
{
    public string DocumentId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string OcrJobId { get; set; } = string.Empty;
}
