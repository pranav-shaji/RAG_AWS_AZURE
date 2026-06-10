using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Ingestion.Models;

namespace AwsRagChat.Ingestion.Services;

public sealed class DocumentIngestionPipeline :
    IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult>
{
    private readonly ChunkingService _chunkingService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IChunkRepository _chunkRepository;
    private readonly IDocumentStatusService _documentStatusService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<DocumentIngestionPipeline> _logger;

    public DocumentIngestionPipeline(
        ChunkingService chunkingService,
        IEmbeddingProvider embeddingProvider,
        IChunkRepository chunkRepository,
        IDocumentStatusService documentStatusService,
        IVectorStore vectorStore,
        ILogger<DocumentIngestionPipeline> logger)
    {
        _chunkingService = chunkingService;
        _embeddingProvider = embeddingProvider;
        _chunkRepository = chunkRepository;
        _documentStatusService = documentStatusService;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task<IngestionPipelineResult> ProcessExtractedDocumentAsync(
        IngestionDocumentRequest request,
        ExtractedDocument extractedDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(extractedDocument);

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "DocumentIngestionPipeline.Process",
            ActivityKind.Internal);

        activity?.SetTag("document.id", request.DocumentId);
        activity?.SetTag("owner.id", request.OwnerUserId);
        activity?.SetTag("file.name", request.FileName);

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(extractedDocument.FullText))
        {
            _logger.LogWarning("No text extracted for document {DocumentId}.", request.DocumentId);

            await _documentStatusService.MarkFailedAsync(
                request.DocumentId,
                request.OwnerUserId,
                request.FileName,
                request.ObjectKey,
                request.EmptyTextErrorMessage,
                cancellationToken);

            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.OcrJobCounter.Add(1, 
                new KeyValuePair<string, object?>("status", "FailedNoText"),
                new KeyValuePair<string, object?>("documentId", request.DocumentId));

            return new IngestionPipelineResult
            {
                Succeeded = false,
                ErrorMessage = request.EmptyTextErrorMessage,
                PageCount = extractedDocument.PageCount
            };
        }

        var chunks = _chunkingService.CreateChunks(
            request.DocumentId,
            request.FileName,
            request.ObjectKey,
            extractedDocument);

        var isAdminDocument = await _documentStatusService.GetIsAdminDocumentAsync(
            request.DocumentId,
            cancellationToken);

        var allowedRoles = await _documentStatusService.GetAllowedRolesAsync(
            request.DocumentId,
            cancellationToken);

        foreach (var chunk in chunks)
        {
            chunk.OwnerUserId = request.OwnerUserId;
            chunk.IsAdminDocument = isAdminDocument;
            chunk.AllowedRoles = allowedRoles;
        }

        _logger.LogInformation("Created {ChunkCount} chunks for document {DocumentId}.", chunks.Count, request.DocumentId);

        foreach (var chunk in chunks.Take(5))
        {
            _logger.LogDebug(
                "Chunk preview. Order: {ChunkOrder}, Page: {PageNumber}, Heading: {Heading}, Length: {Length}, Text: {Text}",
                chunk.ChunkOrder,
                chunk.PageNumber,
                chunk.Heading,
                chunk.Text.Length,
                TrimForLog(chunk.Text, 260));
        }

        await _documentStatusService.MarkOcrCompletedAsync(
            request.DocumentId,
            request.OwnerUserId,
            request.FileName,
            request.ObjectKey,
            request.OcrJobId,
            chunks.Count,
            extractedDocument.PageCount,
            cancellationToken);

        await _documentStatusService.MarkEmbeddingStartedAsync(
            request.DocumentId,
            request.OwnerUserId,
            request.FileName,
            request.ObjectKey,
            cancellationToken);

        _logger.LogInformation("Generating embeddings for {ChunkCount} chunks of document {DocumentId}.", chunks.Count, request.DocumentId);

        foreach (var chunk in chunks)
        {
            chunk.Embedding = await _embeddingProvider.GenerateEmbeddingAsync(
                chunk.Text,
                cancellationToken);

            if (chunk.Embedding.Count == 0)
                throw new InvalidOperationException($"Empty embedding generated for chunk {chunk.ChunkId}.");
        }

        _logger.LogInformation(
            "Generated embeddings. ChunkCount: {ChunkCount}, Dimensions: {Dimensions}",
            chunks.Count,
            chunks.Count > 0 ? chunks[0].Embedding.Count : 0);

        _logger.LogInformation("Persisting {ChunkCount} chunks to document store for document {DocumentId}.", chunks.Count, request.DocumentId);

        await _chunkRepository.SaveChunksAsync(
            chunks,
            cancellationToken);

        await _documentStatusService.MarkIndexingStartedAsync(
            request.DocumentId,
            request.OwnerUserId,
            request.FileName,
            request.ObjectKey,
            cancellationToken);

        _logger.LogInformation("Indexing {ChunkCount} chunks into vector store for document {DocumentId}.", chunks.Count, request.DocumentId);

        foreach (var chunk in chunks)
        {
            _logger.LogDebug(
                "Indexing chunk. DocumentId: {DocumentId}, ChunkId: {ChunkId}, OwnerUserId: {OwnerUserId}, TextLength: {TextLength}",
                chunk.DocumentId,
                chunk.ChunkId,
                chunk.OwnerUserId,
                chunk.Text?.Length ?? 0);

            await _vectorStore.IndexDocumentAsync(chunk);
        }

        await _documentStatusService.MarkIndexedAsync(
            request.DocumentId,
            request.OwnerUserId,
            request.FileName,
            request.ObjectKey,
            chunks.Count,
            extractedDocument.PageCount,
            cancellationToken);

        stopwatch.Stop();
        
        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.OcrDurationHistogram.Record(
            stopwatch.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("operation", "IngestionPipeline"),
            new KeyValuePair<string, object?>("documentId", request.DocumentId));

        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.OcrJobCounter.Add(1, 
            new KeyValuePair<string, object?>("status", "Succeeded"),
            new KeyValuePair<string, object?>("documentId", request.DocumentId));

        _logger.LogInformation(
            "SUCCESS: {ChunkCount} chunks across {PageCount} pages processed and indexed for document {DocumentId} in {ElapsedMs}ms.",
            chunks.Count,
            extractedDocument.PageCount,
            request.DocumentId,
            stopwatch.ElapsedMilliseconds);

        return new IngestionPipelineResult
        {
            Succeeded = true,
            ChunkCount = chunks.Count,
            PageCount = extractedDocument.PageCount
        };
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
