using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.Textract;
using AwsRagChat.Ingestion.Options;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace AwsRagChat.Ingestion.Handlers;

public sealed class S3DocumentIngestionFunction
{
    private readonly IAmazonS3 _amazonS3;
    private readonly TextExtractionService _textExtractionService;
    private readonly TextractTextExtractionService _textractTextExtractionService;
    private readonly TextractAsyncExtractionService _textractAsyncExtractionService;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingBatchService _embeddingBatchService;
    private readonly ChunkPersistenceService _chunkPersistenceService;
    private readonly DocumentStatusService _documentStatusService;
    private readonly OpenSearchService _openSearchService;

    public S3DocumentIngestionFunction()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var dynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(DynamoDbOptions.SectionName).Get<DynamoDbOptions>() ?? new DynamoDbOptions());

        var bedrockOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(BedrockOptions.SectionName).Get<BedrockOptions>() ?? new BedrockOptions());

        var textractAsyncOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(TextractAsyncOptions.SectionName).Get<TextractAsyncOptions>() ?? new TextractAsyncOptions());

        _amazonS3 = new AmazonS3Client();

        var dynamoDb = new AmazonDynamoDBClient();
        var bedrock = new AmazonBedrockRuntimeClient();
        var textract = new AmazonTextractClient();

        _textExtractionService = new TextExtractionService();
        _textractTextExtractionService = new TextractTextExtractionService(textract);
        _textractAsyncExtractionService = new TextractAsyncExtractionService(textract, textractAsyncOptions);
        _chunkingService = new ChunkingService();
        _embeddingBatchService = new EmbeddingBatchService(bedrock, bedrockOptions);
        _chunkPersistenceService = new ChunkPersistenceService(dynamoDb, dynamoDbOptions);
        _documentStatusService = new DocumentStatusService(
            dynamoDb,
            configuration["DynamoDb:DocumentsTableName"] ?? "rag-documents");
        _openSearchService = new OpenSearchService(configuration);
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

                ExtractedDocument extracted;

                if (_textExtractionService.CanExtractDirectly(fileName))
                {
                    using var getObjectResponse = await _amazonS3.GetObjectAsync(bucketName, objectKey);
                    await using var responseStream = getObjectResponse.ResponseStream;

                    extracted = await _textExtractionService.ExtractAsync(
                        fileName,
                        responseStream,
                        CancellationToken.None);

                    if (_textExtractionService.ShouldFallbackToTextract(fileName, extracted))
                    {
                        context.Logger.LogLine("Fallback to Textract async OCR.");

                        var jobId = await _textractAsyncExtractionService.StartDocumentTextDetectionAsync(
                            bucketName,
                            objectKey,
                            CancellationToken.None);

                        await _documentStatusService.MarkOcrStartedAsync(
                            documentId,
                            ownerUserId,
                            fileName,
                            objectKey,
                            jobId,
                            CancellationToken.None);

                        context.Logger.LogLine($"Started Textract job: {jobId}");
                        continue;
                    }
                }
                else if (_textractTextExtractionService.CanExtractWithTextract(fileName))
                {
                    context.Logger.LogLine("Using Textract sync OCR for image document.");

                    extracted = await _textractTextExtractionService.ExtractFromS3Async(
                        bucketName,
                        objectKey,
                        fileName,
                        CancellationToken.None);
                }
                else
                {
                    throw new NotSupportedException($"File type '{Path.GetExtension(fileName)}' is not supported.");
                }

                await ProcessExtractedDocumentAsync(
                    extracted,
                    documentId,
                    ownerUserId,
                    fileName,
                    objectKey,
                    context,
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

    private async Task ProcessExtractedDocumentAsync(
        ExtractedDocument extractedDocument,
        string documentId,
        string ownerUserId,
        string fileName,
        string objectKey,
        ILambdaContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(extractedDocument?.FullText))
        {
            context.Logger.LogLine("No text extracted.");

            await _documentStatusService.MarkFailedAsync(
                documentId,
                ownerUserId,
                fileName,
                objectKey,
                "No extractable text found.",
                cancellationToken);

            return;
        }

        var chunks = _chunkingService.CreateChunks(
            documentId,
            fileName,
            objectKey,
            extractedDocument);

        var isAdminDocument = await _documentStatusService.GetIsAdminDocumentAsync(
            documentId,
            cancellationToken);

        var allowedRoles = await _documentStatusService.GetAllowedRolesAsync(
            documentId,
            cancellationToken);

        foreach (var chunk in chunks)
        {
            chunk.OwnerUserId = ownerUserId;
            chunk.IsAdminDocument = isAdminDocument;
            chunk.AllowedRoles = allowedRoles;
        }

        context.Logger.LogLine($"Created {chunks.Count} chunks.");

        foreach (var chunk in chunks.Take(5))
        {
            context.Logger.LogLine(
                $"Chunk preview. Order: {chunk.ChunkOrder}, Page: {chunk.PageNumber}, Heading: {chunk.Heading}, Length: {chunk.Text.Length}, Text: {TrimForLog(chunk.Text, 260)}");
        }

        await _documentStatusService.MarkOcrCompletedAsync(
            documentId,
            ownerUserId,
            fileName,
            objectKey,
            string.Empty,
            chunks.Count,
            extractedDocument.PageCount,
            cancellationToken);

        await _documentStatusService.MarkEmbeddingStartedAsync(
            documentId,
            ownerUserId,
            fileName,
            objectKey,
            cancellationToken);

        context.Logger.LogLine("Generating embeddings.");

        await _embeddingBatchService.AddEmbeddingsAsync(
            chunks,
            cancellationToken);

        context.Logger.LogLine(
            $"Generated embeddings. ChunkCount: {chunks.Count}, Dimensions: {(chunks.Count > 0 ? chunks[0].Embedding.Count : 0)}");

        context.Logger.LogLine("Saving chunks to DynamoDB.");

        await _chunkPersistenceService.SaveChunksAsync(
            chunks,
            cancellationToken);

        await _documentStatusService.MarkIndexingStartedAsync(
            documentId,
            ownerUserId,
            fileName,
            objectKey,
            cancellationToken);

        context.Logger.LogLine("Indexing chunks into OpenSearch.");

        foreach (var chunk in chunks)
        {
            context.Logger.LogLine(
                $"Indexing chunk. DocumentId: {chunk.DocumentId}, ChunkId: {chunk.ChunkId}, OwnerUserId: {chunk.OwnerUserId}, TextLength: {chunk.Text?.Length ?? 0}");

            await _openSearchService.IndexDocumentAsync(chunk);
        }

        await _documentStatusService.MarkIndexedAsync(
            documentId,
            ownerUserId,
            fileName,
            objectKey,
            chunks.Count,
            extractedDocument.PageCount,
            cancellationToken);

        context.Logger.LogLine(
            $"SUCCESS: {chunks.Count} chunks across {extractedDocument.PageCount} pages processed and indexed for document {documentId}.");
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

    private static string TrimForLog(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
