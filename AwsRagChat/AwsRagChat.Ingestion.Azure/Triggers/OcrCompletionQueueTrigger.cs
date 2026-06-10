using System;
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

public class OcrCompletionQueueTrigger
{
    private readonly IDocumentProcessor _documentProcessor;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> _documentIngestionPipeline;
    private readonly ILogger<OcrCompletionQueueTrigger> _logger;

    public OcrCompletionQueueTrigger(
        IDocumentProcessor documentProcessor,
        IDocumentStatusService documentStatusService,
        IIngestionPipeline<IngestionDocumentRequest, AwsRagChat.Ingestion.Services.ExtractedDocument, IngestionPipelineResult> documentIngestionPipeline,
        ILogger<OcrCompletionQueueTrigger> logger)
    {
        _documentProcessor = documentProcessor;
        _documentStatusService = documentStatusService;
        _documentIngestionPipeline = documentIngestionPipeline;
        _logger = logger;
    }

    [Function("OcrCompletionQueueTrigger")]
    public async Task Run(
        [QueueTrigger("ocr-completion-queue", Connection = "AzureWebJobsStorage")] string messageJson,
        FunctionContext context)
    {
        OcrCompletionMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<OcrCompletionMessage>(messageJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message. Deleting message.");
            return;
        }

        if (msg == null || string.IsNullOrWhiteSpace(msg.OcrJobId))
        {
            _logger.LogError("Invalid queue message payload. Deleting message.");
            return;
        }

        // Start Activity with traceparent if available
        var parentContext = string.IsNullOrWhiteSpace(msg.TraceParent) 
            ? default 
            : System.Diagnostics.ActivityContext.Parse(msg.TraceParent, null);

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "OcrCompletionQueueTrigger",
            System.Diagnostics.ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("document.id", msg.DocumentId);
        activity?.SetTag("user.id", msg.OwnerUserId);
        activity?.SetTag("file.name", msg.FileName);
        activity?.SetTag("ocr.job.id", msg.OcrJobId);
        
        var correlationId = msg.CorrelationId ?? msg.TraceParent ?? Guid.NewGuid().ToString();
        activity?.SetTag("correlation.id", correlationId);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["DocumentId"] = msg.DocumentId,
            ["OwnerUserId"] = msg.OwnerUserId,
            ["OcrJobId"] = msg.OcrJobId
        }))
        {
            _logger.LogInformation("Processing Queue trigger. Message: {message}", messageJson);

            try
            {
                var processingResult = await _documentProcessor.GetCompletedOcrResultAsync(
                    new CompletedOcrRequest
                    {
                        OcrJobId = msg.OcrJobId,
                        BucketOrContainerName = "uploads",
                        ObjectKey = msg.ObjectKey,
                        FileName = msg.FileName
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
                        DocumentId = msg.DocumentId,
                        OwnerUserId = msg.OwnerUserId,
                        FileName = msg.FileName,
                        ObjectKey = msg.ObjectKey,
                        OcrJobId = msg.OcrJobId,
                        EmptyTextErrorMessage = "No extractable text found after Azure Document Intelligence OCR."
                    },
                    extracted,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex.Message.Contains("Status: running", StringComparison.OrdinalIgnoreCase) || 
                                     ex.Message.Contains("Status: notStarted", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Azure Document Intelligence job {jobId} is still running. Retrying via queue visibility...", msg.OcrJobId);
                throw; // Re-throw to keep message on queue and trigger retry back-off.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure Document Intelligence job {jobId} failed permanently: {message}", msg.OcrJobId, ex.Message);

                await _documentStatusService.MarkFailedAsync(
                    msg.DocumentId,
                    msg.OwnerUserId,
                    msg.FileName,
                    msg.ObjectKey,
                    ex.Message,
                    CancellationToken.None);
                
                // Do not re-throw, completing execution and deleting message from queue.
            }
        }
    }
}
