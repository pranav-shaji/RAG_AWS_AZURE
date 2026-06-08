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

    public DocumentIngestionPipeline(
        ChunkingService chunkingService,
        IEmbeddingProvider embeddingProvider,
        IChunkRepository chunkRepository,
        IDocumentStatusService documentStatusService,
        IVectorStore vectorStore)
    {
        _chunkingService = chunkingService;
        _embeddingProvider = embeddingProvider;
        _chunkRepository = chunkRepository;
        _documentStatusService = documentStatusService;
        _vectorStore = vectorStore;
    }

    public async Task<IngestionPipelineResult> ProcessExtractedDocumentAsync(
        IngestionDocumentRequest request,
        ExtractedDocument extractedDocument,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(extractedDocument);

        if (string.IsNullOrWhiteSpace(extractedDocument.FullText))
        {
            log?.Invoke("No text extracted.");

            await _documentStatusService.MarkFailedAsync(
                request.DocumentId,
                request.OwnerUserId,
                request.FileName,
                request.ObjectKey,
                request.EmptyTextErrorMessage,
                cancellationToken);

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

        log?.Invoke($"Created {chunks.Count} chunks.");

        foreach (var chunk in chunks.Take(5))
        {
            log?.Invoke(
                $"Chunk preview. Order: {chunk.ChunkOrder}, Page: {chunk.PageNumber}, Heading: {chunk.Heading}, Length: {chunk.Text.Length}, Text: {TrimForLog(chunk.Text, 260)}");
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

        log?.Invoke("Generating embeddings.");

        foreach (var chunk in chunks)
        {
            chunk.Embedding = await _embeddingProvider.GenerateEmbeddingAsync(
                chunk.Text,
                cancellationToken);

            if (chunk.Embedding.Count == 0)
                throw new InvalidOperationException($"Empty embedding generated for chunk {chunk.ChunkId}.");
        }

        log?.Invoke(
            $"Generated embeddings. ChunkCount: {chunks.Count}, Dimensions: {(chunks.Count > 0 ? chunks[0].Embedding.Count : 0)}");

        log?.Invoke("Persisting chunks to document store.");

        await _chunkRepository.SaveChunksAsync(
            chunks,
            cancellationToken);

        await _documentStatusService.MarkIndexingStartedAsync(
            request.DocumentId,
            request.OwnerUserId,
            request.FileName,
            request.ObjectKey,
            cancellationToken);

        log?.Invoke("Indexing chunks into vector store.");

        foreach (var chunk in chunks)
        {
            log?.Invoke(
                $"Indexing chunk. DocumentId: {chunk.DocumentId}, ChunkId: {chunk.ChunkId}, OwnerUserId: {chunk.OwnerUserId}, TextLength: {chunk.Text?.Length ?? 0}");

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

        log?.Invoke(
            $"SUCCESS: {chunks.Count} chunks across {extractedDocument.PageCount} pages processed and indexed for document {request.DocumentId}.");

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
