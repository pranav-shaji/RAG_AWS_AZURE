using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Aws;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class TextractCompletionFunction
{
    private readonly IDocumentProcessor _documentProcessor;
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;
    private readonly IDocumentStatusService _documentStatusService;

    private readonly IConfiguration _configuration;

    public TextractCompletionFunction()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var services = AwsIngestionComposition.Create(_configuration);

        _documentProcessor = services.DocumentProcessor;
        _documentStatusService = services.DocumentStatusService;
        _documentIngestionPipeline = services.DocumentIngestionPipeline;
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var records = sqsEvent?.Records ?? [];
        var recordCount = records.Count;
        context.Logger.LogLine($"Textract completion SQS record count: {recordCount}");

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
                    context.Logger.LogLine(
                        $"Unable to parse Textract completion message. Error: {parseError}. RawBody: {TrimForLog(record.Body, 2000)}");
                    continue;
                }

                context.Logger.LogLine($"Detected Textract completion message shape: {messageShape}");

                using var innerDoc = JsonDocument.Parse(messageJson);

                jobId = innerDoc.RootElement.TryGetProperty("JobId", out var jobIdElement)
                    ? jobIdElement.GetString() ?? string.Empty
                    : string.Empty;

                status = innerDoc.RootElement.TryGetProperty("Status", out var statusElement)
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

                context.Logger.LogLine(
                    $"Textract completion notification. JobId: {jobId}, Status: {status}");

                if (string.IsNullOrWhiteSpace(jobId))
                {
                    context.Logger.LogLine("Textract notification did not include a JobId.");
                    continue;
                }

                if (!TryReadDocumentLocation(innerDoc.RootElement, out bucket, out key))
                {
                    context.Logger.LogLine(
                        $"Textract notification for job {jobId} did not include a valid S3 location. " +
                        "No existing repository lookup by TextractJobId was found, so the document cannot be resolved from this message.");
                    continue;
                }

                context.Logger.LogLine(
                    $"Textract document location resolved. JobId: {jobId}, Bucket: {bucket}, Key: {key}");

                fileName = Path.GetFileName(key);
                ownerUserId = TryExtractOwnerUserId(key);
                documentId = TryExtractDocumentId(key);

                context.Logger.LogLine(
                    $"Textract document metadata resolved. JobId: {jobId}, DocumentId: {documentId}, OwnerUserId: {ownerUserId}, FileName: {fileName}");

                if (string.IsNullOrWhiteSpace(ownerUserId) ||
                    string.IsNullOrWhiteSpace(documentId) ||
                    string.IsNullOrWhiteSpace(fileName))
                {
                    context.Logger.LogLine($"Invalid Textract document key: {key}");
                    continue;
                }

                if (IsFailedTextractStatus(status))
                {
                    context.Logger.LogLine($"Textract job {jobId} ended with failure status {status}.");

                    await _documentStatusService.MarkFailedAsync(
                        documentId,
                        ownerUserId,
                        fileName,
                        key,
                        $"Textract job ended with status {status ?? "UNKNOWN"}.",
                        CancellationToken.None);

                    continue;
                }

                if (!string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
                {
                    context.Logger.LogLine($"Skipping Textract job {jobId} with non-terminal/non-success status {status}.");
                    continue;
                }

                context.Logger.LogLine($"Processing Textract result for {key}");

                var processingResult = await _documentProcessor.GetCompletedOcrResultAsync(
                    new CompletedOcrRequest
                    {
                        OcrJobId = jobId,
                        BucketOrContainerName = bucket,
                        ObjectKey = key,
                        FileName = fileName
                    },
                    CancellationToken.None);

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

                context.Logger.LogLine(
                    $"Textract extracted document. JobId: {jobId}, TextLength: {extractedDocument.FullText?.Length ?? 0}, PageCount: {extractedDocument.PageCount}");

                context.Logger.LogLine(
                    $"Calling DocumentIngestionPipeline. JobId: {jobId}, DocumentId: {documentId}, Key: {key}");

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
                    context.Logger.LogLine,
                    CancellationToken.None);

                context.Logger.LogLine(
                    $"DocumentIngestionPipeline result. JobId: {jobId}, DocumentId: {documentId}, Succeeded: {pipelineResult.Succeeded}, ChunkCount: {pipelineResult.ChunkCount}, PageCount: {pipelineResult.PageCount}, Error: {pipelineResult.ErrorMessage ?? string.Empty}");
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"ERROR: {ex}");

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

        if (parts.Length >= 4)
            return parts[1];

        return string.Empty;
    }

    private static string TryExtractDocumentId(string objectKey)
    {
        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 4)
            return parts[2];

        return string.Empty;
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
