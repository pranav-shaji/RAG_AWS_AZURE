using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Textract;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Ingestion.Options;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class TextractCompletionFunction
{
    private readonly TextractAsyncExtractionService _textractAsyncExtractionService;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingBatchService _embeddingBatchService;
    private readonly ChunkPersistenceService _chunkPersistenceService;
    private readonly OpenSearchService _openSearchService;
    private readonly DocumentStatusService _documentStatusService;

    private readonly IConfiguration _configuration;

    public TextractCompletionFunction()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var dynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            _configuration.GetSection(DynamoDbOptions.SectionName).Get<DynamoDbOptions>() ?? new DynamoDbOptions());

        var bedrockOptions = Microsoft.Extensions.Options.Options.Create(
            _configuration.GetSection(BedrockOptions.SectionName).Get<BedrockOptions>() ?? new BedrockOptions());

        var textractAsyncOptions = Microsoft.Extensions.Options.Options.Create(
            _configuration.GetSection(TextractAsyncOptions.SectionName).Get<TextractAsyncOptions>() ?? new TextractAsyncOptions());

        var dynamoDb = new AmazonDynamoDBClient();
        var bedrock = new AmazonBedrockRuntimeClient();
        var textract = new AmazonTextractClient();

        _textractAsyncExtractionService = new TextractAsyncExtractionService(textract, textractAsyncOptions);
        _chunkingService = new ChunkingService();
        _embeddingBatchService = new EmbeddingBatchService(bedrock, bedrockOptions);
        _chunkPersistenceService = new ChunkPersistenceService(dynamoDb, dynamoDbOptions);
        _documentStatusService = new DocumentStatusService(
            dynamoDb,
            _configuration["DynamoDb:DocumentsTableName"] ?? "rag-documents");

        _openSearchService = new OpenSearchService(_configuration);
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            try
            {
                context.Logger.LogLine($"RAW SQS BODY: {record.Body}");

                using var outerDoc = JsonDocument.Parse(record.Body);

                if (!outerDoc.RootElement.TryGetProperty("Message", out var messageElement))
                {
                    context.Logger.LogLine("SNS Message field not found.");
                    continue;
                }

                var messageJson = messageElement.GetString();

                if (string.IsNullOrWhiteSpace(messageJson))
                {
                    context.Logger.LogLine("SNS Message is empty.");
                    continue;
                }

                context.Logger.LogLine($"INNER MESSAGE: {messageJson}");

                using var innerDoc = JsonDocument.Parse(messageJson);

                var jobId = innerDoc.RootElement.TryGetProperty("JobId", out var jobIdElement)
                    ? jobIdElement.GetString()
                    : null;

                var status = innerDoc.RootElement.TryGetProperty("Status", out var statusElement)
                    ? statusElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(jobId))
                {
                    context.Logger.LogLine("Textract notification did not include a JobId.");
                    continue;
                }

                if (!TryReadDocumentLocation(innerDoc.RootElement, out var bucket, out var key))
                {
                    context.Logger.LogLine($"Textract notification for job {jobId} did not include a valid S3 location.");
                    continue;
                }

                var fileName = Path.GetFileName(key);
                var ownerUserId = TryExtractOwnerUserId(key);
                var documentId = TryExtractDocumentId(key);

                if (string.IsNullOrWhiteSpace(ownerUserId) ||
                    string.IsNullOrWhiteSpace(documentId) ||
                    string.IsNullOrWhiteSpace(fileName))
                {
                    context.Logger.LogLine($"Invalid Textract document key: {key}");
                    continue;
                }

                if (!string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
                {
                    context.Logger.LogLine($"Skipping job {jobId} with status {status}");

                    await _documentStatusService.MarkFailedAsync(
                        documentId,
                        ownerUserId,
                        fileName,
                        key,
                        $"Textract job ended with status {status ?? "UNKNOWN"}.",
                        CancellationToken.None);

                    continue;
                }

                context.Logger.LogLine($"Processing Textract result for {key}");

                var extractedDocument = await _textractAsyncExtractionService
                    .GetCompletedDocumentAsync(jobId, CancellationToken.None);

                if (string.IsNullOrWhiteSpace(extractedDocument.FullText))
                {
                    context.Logger.LogLine("No text extracted.");

                    await _documentStatusService.MarkFailedAsync(
                        documentId,
                        ownerUserId,
                        fileName,
                        key,
                        "No extractable text found after Textract OCR.",
                        CancellationToken.None);

                    continue;
                }

                var chunks = _chunkingService.CreateChunks(
                    documentId,
                    fileName,
                    key,
                    extractedDocument);

                var isAdminDocument = await _documentStatusService.GetIsAdminDocumentAsync(
                    documentId,
                    CancellationToken.None);

                var allowedRoles = await _documentStatusService.GetAllowedRolesAsync(
                    documentId,
                    CancellationToken.None);

                foreach (var chunk in chunks)
                {
                    chunk.OwnerUserId = ownerUserId;
                    chunk.IsAdminDocument = isAdminDocument;
                    chunk.AllowedRoles = allowedRoles;
                }

                await _documentStatusService.MarkOcrCompletedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    key,
                    jobId,
                    chunks.Count,
                    extractedDocument.PageCount,
                    CancellationToken.None);

                await _documentStatusService.MarkEmbeddingStartedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    key,
                    CancellationToken.None);

                await _embeddingBatchService.AddEmbeddingsAsync(chunks);

                await _chunkPersistenceService.SaveChunksAsync(chunks);

                await _documentStatusService.MarkIndexingStartedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    key,
                    CancellationToken.None);

                foreach (var chunk in chunks)
                {
                    await _openSearchService.IndexDocumentAsync(chunk);
                }

                await _documentStatusService.MarkIndexedAsync(
                    documentId,
                    ownerUserId,
                    fileName,
                    key,
                    chunks.Count,
                    extractedDocument.PageCount,
                    CancellationToken.None);

                context.Logger.LogLine($"SUCCESS: Processed {chunks.Count} chunks across {extractedDocument.PageCount} pages");
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"ERROR: {ex}");
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
}
