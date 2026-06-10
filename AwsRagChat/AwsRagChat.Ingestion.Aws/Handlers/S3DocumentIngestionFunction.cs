using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Aws;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class S3DocumentIngestionFunction
{
    private static readonly ILoggerFactory LoggerFactoryInstance = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
    {
        builder.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
            options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
            {
                Indented = false
            };
        });
    });

    private readonly IDocumentProcessor _documentProcessor;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;
    private readonly ILogger<S3DocumentIngestionFunction> _logger;

    public S3DocumentIngestionFunction()
    {
        var configuration = AwsIngestionConfiguration.Build();
        var services = AwsIngestionComposition.Create(configuration, LoggerFactoryInstance);

        _documentProcessor = services.DocumentProcessor;
        _documentStatusService = services.DocumentStatusService;
        _documentIngestionPipeline = services.DocumentIngestionPipeline;
        _logger = LoggerFactoryInstance.CreateLogger<S3DocumentIngestionFunction>();
    }

    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "S3DocumentIngestionFunction",
            ActivityKind.Consumer);

        if (s3Event?.Records == null || s3Event.Records.Count == 0)
        {
            _logger.LogInformation("No S3 records received.");
            return;
        }

        foreach (var record in s3Event.Records)
        {
            if (record?.S3?.Bucket?.Name == null || record.S3?.Object?.Key == null)
            {
                _logger.LogWarning("Invalid S3 event structure. Skipping record.");
                continue;
            }

            var bucketName = record.S3.Bucket.Name;
            var objectKey = Uri.UnescapeDataString(record.S3.Object.Key.Replace('+', ' '));

            var fileName = Path.GetFileName(objectKey);
            var ownerUserId = TryExtractOwnerUserId(objectKey);
            var documentId = TryExtractDocumentId(objectKey);

            if (string.IsNullOrWhiteSpace(ownerUserId) || string.IsNullOrWhiteSpace(documentId))
            {
                _logger.LogWarning("Invalid key format: {ObjectKey}", objectKey);
                continue;
            }

            // Setup correlation ID from trace ID or GUID
            var correlationId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString();
            activity?.SetTag("correlation.id", correlationId);
            activity?.SetTag("document.id", documentId);
            activity?.SetTag("user.id", ownerUserId);

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["DocumentId"] = documentId,
                ["OwnerUserId"] = ownerUserId
            }))
            {
                _logger.LogInformation("Processing S3 object. Bucket: {Bucket}, Key: {Key}", bucketName, objectKey);

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
                        _logger.LogInformation("Fallback to Textract async OCR. Started job: {JobId}", processingResult.OcrJobId);

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
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ERROR during document ingestion for document {DocumentId}", documentId);

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
