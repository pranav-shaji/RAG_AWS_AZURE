using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Aws;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class TextractCompletionFunction
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
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TextractCompletionFunction> _logger;

    public TextractCompletionFunction()
    {
        _configuration = AwsIngestionConfiguration.Build();
        var services = AwsIngestionComposition.Create(_configuration, LoggerFactoryInstance);

        _documentProcessor = services.DocumentProcessor;
        _documentStatusService = services.DocumentStatusService;
        _documentIngestionPipeline = services.DocumentIngestionPipeline;
        _logger = LoggerFactoryInstance.CreateLogger<TextractCompletionFunction>();
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var records = sqsEvent?.Records ?? [];
        var recordCount = records.Count;
        
        _logger.LogInformation("Textract completion SQS record count: {RecordCount}", recordCount);

        if (recordCount == 0)
            return;

        foreach (var record in records)
        {
            string jobId = string.Empty;
            string status = string.Empty;
            string bucket = string.Empty;
            string key = string.Empty;
            string fileName = string.Empty;
            string ownerUserId = string.Empty;
            string documentId = string.Empty;

            try
            {
                if (!TryGetTextractMessageJson(
                        record.Body,
                        out var messageJson,
                        out var messageShape,
                        out var parseError))
                {
                    _logger.LogWarning(
                        "Unable to parse Textract completion message. Error: {Error}. RawBody: {RawBody}",
                        parseError,
                        TrimForLog(record.Body, 2000));
                    continue;
                }

                _logger.LogInformation("Detected Textract completion message shape: {MessageShape}", messageShape);

                using var innerDoc = JsonDocument.Parse(messageJson);

                jobId = innerDoc.RootElement.TryGetProperty("JobId", out var jobIdElement)
                    ? jobIdElement.GetString() ?? string.Empty
                    : string.Empty;

                status = innerDoc.RootElement.TryGetProperty("Status", out var statusElement)
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

                var jobTag = innerDoc.RootElement.TryGetProperty("JobTag", out var jobTagElement)
                    ? jobTagElement.GetString()
                    : null;

                // Propagate tracing context from JobTag if it is a W3C traceparent
                ActivityContext parentContext = default;
                string? traceParent = null;
                string? correlationId = null;

                if (!string.IsNullOrEmpty(jobTag) && jobTag.StartsWith("00-") && jobTag.Length >= 55)
                {
                    traceParent = jobTag;
                    try
                    {
                        parentContext = ActivityContext.Parse(traceParent, null);
                        correlationId = parentContext.TraceId.ToString();
                    }
                    catch
                    {
                        // Ignore parsing errors
                    }
                }

                correlationId ??= Guid.NewGuid().ToString();

                using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
                    "TextractCompletionFunction",
                    ActivityKind.Consumer,
                    parentContext);

                activity?.SetTag("ocr.job.id", jobId);
                activity?.SetTag("correlation.id", correlationId);

                _logger.LogInformation(
                    "Textract completion notification. JobId: {JobId}, Status: {Status}, JobTag: {JobTag}",
                    jobId,
                    status,
                    jobTag ?? "None");

                if (string.IsNullOrWhiteSpace(jobId))
                {
                    _logger.LogWarning("Textract notification did not include a JobId.");
                    continue;
                }

                if (!TryReadDocumentLocation(innerDoc.RootElement, out bucket, out key))
                {
                    _logger.LogWarning(
                        "Textract notification for job {JobId} did not include a valid S3 location. " +
                        "No existing repository lookup by TextractJobId was found, so the document cannot be resolved from this message.",
                        jobId);
                    continue;
                }

                _logger.LogInformation(
                    "Textract document location resolved. JobId: {JobId}, Bucket: {Bucket}, Key: {Key}",
                    jobId,
                    bucket,
                    key);

                fileName = Path.GetFileName(key);
                ownerUserId = TryExtractOwnerUserId(key);
                documentId = TryExtractDocumentId(key);

                activity?.SetTag("document.id", documentId);
                activity?.SetTag("user.id", ownerUserId);
                activity?.SetTag("file.name", fileName);

                using (_logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId,
                    ["DocumentId"] = documentId,
                    ["OwnerUserId"] = ownerUserId,
                    ["OcrJobId"] = jobId
                }))
                {
                    _logger.LogInformation(
                        "Textract document metadata resolved. JobId: {JobId}, DocumentId: {DocumentId}, OwnerUserId: {OwnerUserId}, FileName: {FileName}",
                        jobId,
                        documentId,
                        ownerUserId,
                        fileName);

                    if (string.IsNullOrWhiteSpace(ownerUserId) ||
                        string.IsNullOrWhiteSpace(documentId) ||
                        string.IsNullOrWhiteSpace(fileName))
                    {
                        _logger.LogWarning("Invalid Textract document key: {Key}", key);
                        continue;
                    }

                    if (IsFailedTextractStatus(status))
                    {
                        _logger.LogWarning("Textract job {JobId} ended with failure status {Status}.", jobId, status);

                        await _documentStatusService.MarkFailedAsync(
                            documentId,
                            ownerUserId,
                            fileName,
                            key,
                            $"Textract job ended with status {status ?? "UNKNOWN"}.",
                            CancellationToken.None);

                        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.OcrJobCounter.Add(1, 
                            new KeyValuePair<string, object?>("status", "FailedTextract"),
                            new KeyValuePair<string, object?>("documentId", documentId));

                        continue;
                    }

                    if (!string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Skipping Textract job {JobId} with non-terminal/non-success status {Status}.", jobId, status);
                        continue;
                    }

                    _logger.LogInformation("Processing Textract result for {Key}", key);

                    var stopwatch = Stopwatch.StartNew();

                    var processingResult = await _documentProcessor.GetCompletedOcrResultAsync(
                        new CompletedOcrRequest
                        {
                            OcrJobId = jobId,
                            BucketOrContainerName = bucket,
                            ObjectKey = key,
                            FileName = fileName
                        },
                        CancellationToken.None);

                    stopwatch.Stop();

                    AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.OcrDurationHistogram.Record(
                        stopwatch.ElapsedMilliseconds,
                        new KeyValuePair<string, object?>("operation", "TextractOcr"),
                        new KeyValuePair<string, object?>("documentId", documentId));

                    if (processingResult.Status == DocumentProcessingStatus.Failed)
                    {
                        throw new InvalidOperationException(processingResult.Message ?? "OCR retrieval failed.");
                    }

                    if (processingResult.ExtractedDocument == null)
                    {
                        throw new InvalidOperationException("No OCR extracted document content was returned.");
                    }

                    var extractedDocument = new AwsRagChat.Ingestion.Services.ExtractedDocument
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

                    _logger.LogInformation(
                        "Textract extracted document. JobId: {JobId}, TextLength: {TextLength}, PageCount: {PageCount}",
                        jobId,
                        extractedDocument.FullText?.Length ?? 0,
                        extractedDocument.PageCount);

                    _logger.LogInformation(
                        "Calling DocumentIngestionPipeline. JobId: {JobId}, DocumentId: {DocumentId}, Key: {Key}",
                        jobId,
                        documentId,
                        key);

                    var pipelineResult = await _documentIngestionPipeline.ProcessExtractedDocumentAsync(
                        new IngestionDocumentRequest
                        {
                            DocumentId = documentId,
                            OwnerUserId = ownerUserId,
                            FileName = fileName,
                            ObjectKey = key,
                            OcrJobId = jobId,
                            EmptyTextErrorMessage = "No extractable text found after Textract OCR."
                        },
                        extractedDocument,
                        CancellationToken.None);

                    _logger.LogInformation(
                        "DocumentIngestionPipeline result. JobId: {JobId}, DocumentId: {DocumentId}, Succeeded: {Succeeded}, ChunkCount: {ChunkCount}, PageCount: {PageCount}, Error: {Error}",
                        jobId,
                        documentId,
                        pipelineResult.Succeeded,
                        pipelineResult.ChunkCount,
                        pipelineResult.PageCount,
                        pipelineResult.ErrorMessage ?? "None");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ERROR processing Textract message for JobId {JobId}", jobId);

                if (!string.IsNullOrWhiteSpace(documentId) &&
                    !string.IsNullOrWhiteSpace(ownerUserId) &&
                    !string.IsNullOrWhiteSpace(fileName) &&
                    !string.IsNullOrWhiteSpace(key))
                {
                    await _documentStatusService.MarkFailedAsync(
                        documentId,
                        ownerUserId,
                        fileName,
                        key,
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

    private static bool TryReadDocumentLocation(
        JsonElement root,
        out string bucket,
        out string key)
    {
        bucket = string.Empty;
        key = string.Empty;

        if (!root.TryGetProperty("DocumentLocation", out var location))
            return false;

        bucket = location.TryGetProperty("S3Bucket", out var bucketElement)
            ? bucketElement.GetString() ?? string.Empty
            : string.Empty;

        key = location.TryGetProperty("S3ObjectName", out var keyElement)
            ? keyElement.GetString() ?? string.Empty
            : string.Empty;

        return !string.IsNullOrWhiteSpace(bucket) &&
               !string.IsNullOrWhiteSpace(key);
    }

    private static bool TryGetTextractMessageJson(
        string? recordBody,
        out string messageJson,
        out string messageShape,
        out string error)
    {
        messageJson = string.Empty;
        messageShape = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(recordBody))
        {
            error = "SQS record body is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(recordBody);
            var root = document.RootElement;

            if (root.TryGetProperty("Message", out var messageElement))
            {
                messageJson = messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? string.Empty
                    : messageElement.GetRawText();

                messageShape = "SNS envelope";

                if (string.IsNullOrWhiteSpace(messageJson))
                {
                    error = "SNS Message is empty.";
                    return false;
                }

                return true;
            }

            if (root.TryGetProperty("JobId", out _) ||
                root.TryGetProperty("Status", out _) ||
                root.TryGetProperty("DocumentLocation", out _))
            {
                messageJson = root.GetRawText();
                messageShape = "Raw Textract message";
                return true;
            }

            error = "Message is neither an SNS envelope nor a raw Textract completion message.";
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsFailedTextractStatus(string? status)
    {
        return string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "PARTIAL_SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
