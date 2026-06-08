using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Aws;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
using Microsoft.Extensions.Configuration;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class S3DocumentIngestionFunction
{
    private readonly IDocumentProcessor _documentProcessor;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;

    public S3DocumentIngestionFunction()
    {
        var configuration = AwsIngestionConfiguration.Build();

        var services = AwsIngestionComposition.Create(configuration);

        _documentProcessor = services.DocumentProcessor;
        _documentStatusService = services.DocumentStatusService;
        _documentIngestionPipeline = services.DocumentIngestionPipeline;
    }

    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        if (s3Event?.Records == null || s3Event.Records.Count == 0)
        {
            context.Logger.LogLine("No S3 records received.");
            return;
        }

        foreach (var record in s3Event.Records)
        {
            if (record?.S3?.Bucket?.Name == null || record.S3?.Object?.Key == null)
            {
                context.Logger.LogLine("Invalid S3 event structure. Skipping record.");
                continue;
            }

            var bucketName = record.S3.Bucket.Name;
            var objectKey = Uri.UnescapeDataString(record.S3.Object.Key.Replace('+', ' '));

            context.Logger.LogLine($"Processing S3 object. Bucket: {bucketName}, Key: {objectKey}");

            var fileName = Path.GetFileName(objectKey);
            var ownerUserId = TryExtractOwnerUserId(objectKey);
            var documentId = TryExtractDocumentId(objectKey);

            if (string.IsNullOrWhiteSpace(ownerUserId) || string.IsNullOrWhiteSpace(documentId))
            {
                context.Logger.LogLine($"Invalid key format: {objectKey}");
                continue;
            }

            try
            {
                await _documentStatusService.MarkUploadedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    objectKey,
                    CancellationToken.None);

                await _documentStatusService.MarkProcessingAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    objectKey,
                    CancellationToken.None);

                var processingResult = await _documentProcessor.ExtractAsync(
                    new DocumentProcessingRequest
                    {
                        BucketOrContainerName = bucketName,
                        ObjectKey = objectKey,
                        FileName = fileName
                    },
                    CancellationToken.None);

                if (processingResult.Status == DocumentProcessingStatus.OcrStarted)
                {
                    context.Logger.LogLine($"Fallback to Textract async OCR. Started job: {processingResult.OcrJobId}");

                    await _documentStatusService.MarkOcrStartedAsync(
                        documentId,
                        ownerUserId,
                        fileName,
                        objectKey,
                        processingResult.OcrJobId!,
                        CancellationToken.None);

                    continue;
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
                        OwnerUserId = ownerUserId,
                        FileName = fileName,
                        ObjectKey = objectKey,
                        EmptyTextErrorMessage = "No extractable text found."
                    },
                    extracted,
                    context.Logger.LogLine,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"ERROR: {ex}");

                await _documentStatusService.MarkFailedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    objectKey,
                    ex.Message,
                    CancellationToken.None);
            }
        }
    }

    private static string TryExtractOwnerUserId(string objectKey)
    {
        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 ? parts[1] : string.Empty;
    }

    private static string TryExtractDocumentId(string objectKey)
    {
        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 ? parts[2] : string.Empty;
    }

}
