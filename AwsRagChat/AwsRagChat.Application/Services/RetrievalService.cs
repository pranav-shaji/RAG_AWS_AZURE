using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AwsRagChat.Application.Services;

public sealed class RetrievalService
{
    private const int RetrievalTopK = 12;
    private const int SummaryTopK = 18;
    private const int MaxFallbackDocuments = 24;
    private const int MaxFallbackCandidateChunks = 600;
    private const double MinimumRankedChunkScore = 0.18;
    private const string ChatCacheVersion = "v3";
    private const string GroundedNoResultMessage = "I could not find that information in the available enterprise documents.";

    private readonly IVectorSearchService _vectorSearchService;
    private readonly IChunkRepository _chunkRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChatCompletionService _chatCompletionService;
    private readonly ILogger<RetrievalService> _logger;
    private readonly IRedisCacheService _redisCacheService;

    private sealed record KeywordProfile(
        string NormalizedQuery,
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> Phrases);

    public RetrievalService(
        IVectorSearchService vectorSearchService,
        IChunkRepository chunkRepository,
        IDocumentRepository documentRepository,
        IEmbeddingService embeddingService,
        IChatCompletionService chatCompletionService,
        ILogger<RetrievalService> logger,
        IRedisCacheService redisCacheService)
    {
        _vectorSearchService = vectorSearchService;
        _chunkRepository = chunkRepository;
        _documentRepository = documentRepository;
        _embeddingService = embeddingService;
        _chatCompletionService = chatCompletionService;
        _logger = logger;
        _redisCacheService = redisCacheService;
    }

    public async Task<ChatAnswer> AskAsync(
        string ownerUserId,
        string currentUserRole,
        string? documentId,
        string question,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat = "text",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question is required.", nameof(question));

        if (string.IsNullOrWhiteSpace(currentUserRole))
            throw new ArgumentException("Current user role is required.", nameof(currentUserRole));

        var totalStopwatch = Stopwatch.StartNew();

        var normalizedQuestion = Regex
    .Replace(
        question.Trim().ToLowerInvariant(),
        @"\s+",
        " ");

        var normalizedOutputFormat = string.IsNullOrWhiteSpace(outputFormat)
            ? "text"
            : outputFormat.Trim().ToLowerInvariant();

        var cacheKey = BuildChatCacheKey(
            ownerUserId,
            currentUserRole,
            documentId,
            normalizedOutputFormat,
            normalizedQuestion);

        _logger.LogInformation(
    "Checking Redis cache. CacheKey={CacheKey}",
    cacheKey);

        ChatAnswer? cachedAnswer = null;
        var cacheStopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "Retrieval timing. Stage=RedisReadStart, CacheKey={CacheKey}",
                cacheKey);

            cachedAnswer =
                await _redisCacheService.GetAsync<ChatAnswer>(cacheKey);

            cacheStopwatch.Stop();

            _logger.LogInformation(
                "Retrieval timing. Stage=RedisReadEnd, CacheKey={CacheKey}, DurationMs={DurationMs}, CacheHit={CacheHit}",
                cacheKey,
                cacheStopwatch.ElapsedMilliseconds,
                cachedAnswer is not null);

            if (cachedAnswer is not null)
            {
                totalStopwatch.Stop();

                _logger.LogInformation(
                    "Redis cache hit. CacheKey={CacheKey}, CacheHit={CacheHit}, CacheDurationMs={CacheDurationMs}, TotalDurationMs={TotalDurationMs}, Question={Question}",
                    cacheKey,
                    true,
                    cacheStopwatch.ElapsedMilliseconds,
                    totalStopwatch.ElapsedMilliseconds,
                    question);

                return cachedAnswer;
            }
        }
        catch (Exception ex)
        {
            cacheStopwatch.Stop();

            _logger.LogWarning(
                ex,
                "Redis cache read failed. DurationMs={DurationMs}. Continuing without cache.",
                cacheStopwatch.ElapsedMilliseconds);
        }

        _logger.LogInformation(
    "Redis cache MISS. CacheKey={CacheKey}, CacheHit={CacheHit}, DurationMs={DurationMs}, Question={Question}",
    cacheKey,
    false,
    cacheStopwatch.ElapsedMilliseconds,
    question);

        var semanticQuestion = question.Trim();
        var searchSharedAdminDocuments = string.IsNullOrWhiteSpace(documentId);
        var sharedDocumentsStopwatch = Stopwatch.StartNew();
        IReadOnlyList<ExistingDocumentRecord> sharedAdminDocuments = searchSharedAdminDocuments
            ? await GetSearchableAdminDocumentsAsync(currentUserRole, cancellationToken)
            : Array.Empty<ExistingDocumentRecord>();
        sharedDocumentsStopwatch.Stop();
        var sharedDocumentIds = sharedAdminDocuments
            .Select(document => document.DocumentId)
            .ToList();
        var sharedOwnerUserIds = sharedAdminDocuments
            .Select(document => document.OwnerUserId)
            .Where(ownerId => !string.IsNullOrWhiteSpace(ownerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Shared/global retrieval scope resolved. CurrentUserId={OwnerUserId}, CurrentUserRole={CurrentUserRole}, SharedGlobalMode={SharedGlobalMode}, SharedAdminDocumentCount={SharedAdminDocumentCount}, SharedOwnerUserIds={SharedOwnerUserIds}, SharedDocumentIds={SharedDocumentIds}",
            ownerUserId,
            currentUserRole,
            searchSharedAdminDocuments,
            sharedAdminDocuments.Count,
            string.Join(",", sharedOwnerUserIds),
            string.Join(",", sharedDocumentIds.Take(12)));

        _logger.LogInformation(
            "Shared/global retrieval scope loaded. DurationMs={DurationMs}, SharedAdminDocumentCount={SharedAdminDocumentCount}",
            sharedDocumentsStopwatch.ElapsedMilliseconds,
            sharedAdminDocuments.Count);

        if (IsDocumentOverviewRequest(semanticQuestion))
        {
            _logger.LogInformation(
                "Retrieval intent detected. Intent=Summary, SummaryMode={SummaryMode}, UserId={OwnerUserId}, DocumentId={DocumentId}, SharedGlobalMode={SharedGlobalMode}",
                true,
                ownerUserId,
                documentId ?? "(all)",
                searchSharedAdminDocuments);

            var overviewStopwatch = Stopwatch.StartNew();
            var overviewChunks = await GetDocumentOverviewChunksAsync(
                ownerUserId,
                currentUserRole,
                documentId,
                sharedAdminDocuments,
                SummaryTopK,
                cancellationToken);
            overviewStopwatch.Stop();

            _logger.LogInformation(
                "Document summary chunks ready for LLM. UserId={OwnerUserId}, DocumentId={DocumentId}, FinalChunkCount={FinalChunkCount}, ChunkPages={ChunkPages}, ChunkIds={ChunkIds}",
                ownerUserId,
                documentId ?? "(all)",
                overviewChunks.Count,
                string.Join(",", overviewChunks.Select(x => x.PageNumber).Distinct().OrderBy(x => x).Take(20)),
                string.Join(",", overviewChunks.Select(x => x.ChunkId).Take(8)));

            _logger.LogInformation(
                "Retrieval timing. Stage=OverviewChunkLoad, DurationMs={DurationMs}",
                overviewStopwatch.ElapsedMilliseconds);

            if (overviewChunks.Count == 0)
                return await BuildAndCacheNoResultAsync(cacheKey, totalStopwatch, cancellationToken);

            return await GenerateAndCacheAnswerAsync(
                cacheKey,
                question,
                overviewChunks,
                conversationHistory,
                conversationSummary,
                outputFormat,
                cancellationToken,
                totalStopwatch,
                allowContextCitationFallback: true);
        }

        var retrievalQuery =
            BuildRetrievalQuery(
                semanticQuestion,
                conversationHistory);

        var embeddingStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Retrieval timing. Stage=EmbeddingStart");

        var questionEmbedding = await _embeddingService.GenerateEmbeddingAsync(
            retrievalQuery,
            cancellationToken);
        embeddingStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval started. UserId={OwnerUserId}, DocumentId={DocumentId}, GlobalMode={GlobalMode}, QueryLength={QueryLength}, EmbeddingDimensions={EmbeddingDimensions}",
            ownerUserId,
            documentId ?? "(all)",
            string.IsNullOrWhiteSpace(documentId),
            retrievalQuery.Length,
            questionEmbedding.Count);

        _logger.LogInformation(
            "Retrieval timing. Stage=EmbeddingEnd, DurationMs={DurationMs}",
            embeddingStopwatch.ElapsedMilliseconds);

        IReadOnlyList<DocumentChunk> vectorChunks;
        var vectorSearchStopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "Retrieval timing. Stage=VectorSearchStart, UserId={OwnerUserId}, DocumentId={DocumentId}, SharedGlobalMode={SharedGlobalMode}",
                ownerUserId,
                documentId ?? "(all)",
                searchSharedAdminDocuments);

            vectorChunks = await _vectorSearchService.SearchAsync(
                ownerUserId,
                documentId,
                questionEmbedding,
                topK: RetrievalTopK,
                searchSharedAdminDocuments: searchSharedAdminDocuments,
                sharedDocumentIds: sharedDocumentIds,
                currentUserRole: currentUserRole,
                cancellationToken: cancellationToken);
            vectorSearchStopwatch.Stop();

            _logger.LogInformation(
                "Retrieval timing. Stage=VectorSearchEnd, DurationMs={DurationMs}, ResultCount={ResultCount}, Success={Success}",
                vectorSearchStopwatch.ElapsedMilliseconds,
                vectorChunks.Count,
                true);
        }
        catch (Exception ex)
        {
            vectorSearchStopwatch.Stop();

            _logger.LogInformation(
                "Retrieval timing. Stage=VectorSearchEnd, DurationMs={DurationMs}, ResultCount={ResultCount}, Success={Success}",
                vectorSearchStopwatch.ElapsedMilliseconds,
                0,
                false);

            _logger.LogWarning(
                ex,
                "Vector search retrieval failed; falling back to DynamoDB chunk search. UserId={OwnerUserId}, DocumentId={DocumentId}",
                ownerUserId,
                documentId ?? "(all)");

            vectorChunks = [];
        }

        var rankingStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Retrieval timing. Stage=HybridRankingStart, VectorSearchHits={VectorSearchHits}",
            vectorChunks.Count);

        var relevantChunks = await HybridRankChunksAsync(
            ownerUserId,
            documentId,
            question,
            questionEmbedding,
            vectorChunks,
            sharedAdminDocuments,
            currentUserRole,
            RetrievalTopK,
            cancellationToken);
        rankingStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=HybridRankingEnd, UserId={OwnerUserId}, DocumentId={DocumentId}, ChunkCount={ChunkCount}, ChunkIds={ChunkIds}, RankingDurationMs={RankingDurationMs}",
            ownerUserId,
            documentId ?? "(all)",
            relevantChunks.Count,
            string.Join(",", relevantChunks.Select(x => x.ChunkId).Take(8)),
            rankingStopwatch.ElapsedMilliseconds);

        if (relevantChunks.Count == 0)
        {
            return await BuildAndCacheNoResultAsync(cacheKey, totalStopwatch, cancellationToken);
        }

        return await GenerateAndCacheAnswerAsync(
            cacheKey,
            question,
            relevantChunks,
            conversationHistory,
            conversationSummary,
            outputFormat,
            cancellationToken,
            totalStopwatch);
    }

    private async Task<ChatAnswer> GenerateAndCacheAnswerAsync(
        string cacheKey,
        string question,
        IReadOnlyList<DocumentChunk> relevantChunks,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat,
        CancellationToken cancellationToken,
        Stopwatch totalStopwatch,
        bool allowContextCitationFallback = false)
    {
        var generationStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Retrieval timing. Stage=GenerationStart, ChunkCount={ChunkCount}",
            relevantChunks.Count);

        var answerText = await _chatCompletionService.GenerateAnswerAsync(
            question,
            relevantChunks,
            conversationHistory,
            conversationSummary,
            outputFormat,
            cancellationToken);
        generationStopwatch.Stop();

        var citationStopwatch = Stopwatch.StartNew();
        var citations = IsGroundedRefusal(answerText)
            ? []
            : BuildCitationsFromAnswer(answerText, relevantChunks, allowContextCitationFallback);

        answerText = CleanInternalSourceMarkers(answerText);
        citationStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=GenerationEnd, GenerationDurationMs={GenerationDurationMs}, CitationDurationMs={CitationDurationMs}, CitationCount={CitationCount}, CitationPages={CitationPages}",
            generationStopwatch.ElapsedMilliseconds,
            citationStopwatch.ElapsedMilliseconds,
            citations.Count,
            string.Join(",", citations.Select(citation => citation.PageNumber).Where(page => page > 0).Distinct().OrderBy(page => page)));

        var response = new ChatAnswer
        {
            Answer = answerText,
            Citations = citations
        };

        try
        {
            var cacheWriteStopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Retrieval timing. Stage=RedisWriteStart, CacheKey={CacheKey}",
                cacheKey);

            await _redisCacheService.SetAsync(
                cacheKey,
                response,
                TimeSpan.FromMinutes(30));
            cacheWriteStopwatch.Stop();

            _logger.LogInformation(
                "Retrieval timing. Stage=RedisWriteEnd. Chat response cached in Redis. CacheKey={CacheKey}, DurationMs={DurationMs}",
                cacheKey,
                cacheWriteStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to cache chat response.");
        }

        totalStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=TotalRetrievalAndGeneration, DurationMs={DurationMs}",
            totalStopwatch.ElapsedMilliseconds);

        return response;
    }

    private async Task<ChatAnswer> BuildAndCacheNoResultAsync(
        string cacheKey,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        var noResult = new ChatAnswer
        {
            Answer = GroundedNoResultMessage,
            Citations = []
        };

        try
        {
            var cacheWriteStopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Retrieval timing. Stage=RedisWriteStart, CacheKey={CacheKey}",
                cacheKey);

            await _redisCacheService.SetAsync(
                cacheKey,
                noResult,
                TimeSpan.FromMinutes(10));
            cacheWriteStopwatch.Stop();

            _logger.LogInformation(
                "Retrieval timing. Stage=RedisWriteEnd. No-result response cached in Redis. CacheKey={CacheKey}, DurationMs={DurationMs}",
                cacheKey,
                cacheWriteStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to cache no-result response.");
        }

        totalStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=TotalRetrievalNoResult, DurationMs={DurationMs}",
            totalStopwatch.ElapsedMilliseconds);

        return noResult;
    }

    private async Task<IReadOnlyList<DocumentChunk>> GetDocumentOverviewChunksAsync(
        string ownerUserId,
        string currentUserRole,
        string? documentId,
        IReadOnlyList<ExistingDocumentRecord> sharedAdminDocuments,
        int topK,
        CancellationToken cancellationToken)
    {
        var chunks = await LoadPersistedChunksAsync(
            ownerUserId,
            currentUserRole,
            documentId,
            sharedAdminDocuments,
            maxChunks: null,
            cancellationToken);

        var selectedChunks = chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .OrderBy(chunk => chunk.DocumentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chunk => chunk.ChunkOrder)
            .Take(Math.Clamp(topK, 1, 30))
            .ToList();

        _logger.LogInformation(
            "Document summary direct chunk load completed. UserId={OwnerUserId}, DocumentId={DocumentId}, SummaryMode={SummaryMode}, TotalChunksLoaded={TotalChunksLoaded}, FinalChunkCount={FinalChunkCount}, ChunkPagesSelected={ChunkPagesSelected}",
            ownerUserId,
            documentId ?? "(all)",
            true,
            chunks.Count,
            selectedChunks.Count,
            string.Join(",", selectedChunks.Select(chunk => chunk.PageNumber).Distinct().OrderBy(page => page).Take(20)));

        return selectedChunks;
    }

    private async Task<IReadOnlyList<DocumentChunk>> HybridRankChunksAsync(
        string ownerUserId,
        string? documentId,
        string question,
        IReadOnlyList<float> queryEmbedding,
        IReadOnlyList<DocumentChunk> vectorChunks,
        IReadOnlyList<ExistingDocumentRecord> sharedAdminDocuments,
        string currentUserRole,
        int topK,
        CancellationToken cancellationToken)
    {
        var keywordProfile = BuildKeywordProfile(question);
        var usableVectorChunks = vectorChunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .Where(chunk => ChunkAllowsRole(chunk, currentUserRole, sharedAdminDocuments))
            .Take(Math.Clamp(topK, 1, 25))
            .ToList();

        if (usableVectorChunks.Count > 0)
        {
            var rankedVectorChunks = usableVectorChunks
                .Select((chunk, index) => new
                {
                    Chunk = chunk,
                    KeywordScore = ComputeKeywordScore(keywordProfile, chunk),
                    VectorSearchRankBoost = Math.Max(0, usableVectorChunks.Count - index) * 0.01
                })
                .Select(x => new
                {
                    x.Chunk,
                    x.KeywordScore,
                    x.VectorSearchRankBoost,
                    TotalScore = x.KeywordScore + x.VectorSearchRankBoost
                })
                .OrderByDescending(x => x.TotalScore)
                .ThenBy(x => x.Chunk.ChunkOrder)
                .ToList();

            _logger.LogInformation(
                "Hybrid retrieval used vector search semantic hits without database full-chunk scan. UserId={OwnerUserId}, DocumentId={DocumentId}, VectorSearchHits={VectorSearchHits}, ReturnedChunks={ReturnedChunks}",
                ownerUserId,
                documentId ?? "(all)",
                vectorChunks.Count,
                rankedVectorChunks.Count);

            return rankedVectorChunks
                .Select(x => x.Chunk)
                .ToList();
        }

        var fallbackDocuments = string.IsNullOrWhiteSpace(documentId)
            ? SelectFallbackDocuments(sharedAdminDocuments, keywordProfile)
            : sharedAdminDocuments;

        var loadStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Retrieval timing. Stage=DatabaseFallbackChunkLoadStart, UserId={OwnerUserId}, DocumentId={DocumentId}, SharedAdminDocumentCount={SharedAdminDocumentCount}, SelectedFallbackDocumentCount={SelectedFallbackDocumentCount}, MaxFallbackCandidateChunks={MaxFallbackCandidateChunks}",
            ownerUserId,
            documentId ?? "(all)",
            sharedAdminDocuments.Count,
            fallbackDocuments.Count,
            MaxFallbackCandidateChunks);

        var persistedChunks = await LoadPersistedChunksAsync(
            ownerUserId,
            currentUserRole,
            documentId,
            fallbackDocuments,
            MaxFallbackCandidateChunks,
            cancellationToken);
        loadStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=DatabaseFallbackChunkLoadEnd, DurationMs={DurationMs}, LoadedChunkCount={LoadedChunkCount}",
            loadStopwatch.ElapsedMilliseconds,
            persistedChunks.Count);

        if (persistedChunks.Count == 0)
        {
            _logger.LogWarning(
                "No persisted chunks were available for hybrid reranking. Falling back to vector search hits. UserId={OwnerUserId}, DocumentId={DocumentId}",
                ownerUserId,
                documentId ?? "(all)");

            return vectorChunks
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                .Where(chunk => ChunkAllowsRole(chunk, currentUserRole, sharedAdminDocuments))
                .Take(Math.Clamp(topK, 1, 25))
                .ToList();
        }

        var vectorHitIds = vectorChunks
            .Select(chunk => chunk.ChunkId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rankComputeStopwatch = Stopwatch.StartNew();
        var queryEmbeddingMagnitude = Magnitude(queryEmbedding);

        var rankedChunks = persistedChunks
            .Where(chunk =>
                !string.IsNullOrWhiteSpace(chunk.Text))
            .Select(chunk => new
            {
                Chunk = chunk,
                VectorScore = chunk.Embedding.Count == queryEmbedding.Count
                    ? CosineSimilarity(queryEmbedding, queryEmbeddingMagnitude, chunk.Embedding)
                    : 0,
                KeywordScore = ComputeKeywordScore(keywordProfile, chunk),
                VectorSearchBoost = vectorHitIds.Contains(chunk.ChunkId) ? 0.15 : 0
            })
            .Select(x => new
            {
                x.Chunk,
                x.VectorScore,
                x.KeywordScore,
                x.VectorSearchBoost,
                TotalScore = (x.VectorScore * 0.90) + (x.KeywordScore * 0.10) + x.VectorSearchBoost
            })
            .OrderByDescending(x => x.TotalScore)
            .ThenBy(x => x.Chunk.ChunkOrder)
            .Take(Math.Clamp(topK, 1, 25))
            .ToList();
        rankComputeStopwatch.Stop();

        _logger.LogInformation(
            "Retrieval timing. Stage=HybridRankingComputeEnd, DurationMs={DurationMs}, CandidateChunks={CandidateChunks}, RankedChunks={RankedChunks}",
            rankComputeStopwatch.ElapsedMilliseconds,
            persistedChunks.Count,
            rankedChunks.Count);

        var topScore =
    rankedChunks.FirstOrDefault()?.TotalScore ?? 0;

        var thresholdedChunks = rankedChunks
            .Where(x => x.TotalScore >= MinimumRankedChunkScore)
            .ToList();

        _logger.LogInformation(
            "Top retrieval score: {TopScore:F4}",
            topScore);

        foreach (var ranked in thresholdedChunks.Take(8))
        {
            _logger.LogInformation(
                "Hybrid retrieval hit. ChunkId={ChunkId}, Page={PageNumber}, Heading={Heading}, VectorScore={VectorScore:F4}, KeywordScore={KeywordScore:F4}, VectorSearchBoost={VectorSearchBoost:F2}, TotalScore={TotalScore:F4}, Preview={Preview}",
                ranked.Chunk.ChunkId,
                ranked.Chunk.PageNumber,
                string.IsNullOrWhiteSpace(ranked.Chunk.Heading) ? ranked.Chunk.Section : ranked.Chunk.Heading,
                ranked.VectorScore,
                ranked.KeywordScore,
                ranked.VectorSearchBoost,
                ranked.TotalScore,
                BuildCitationSnippet(ranked.Chunk.Text));
        }

        _logger.LogInformation(
            "Hybrid retrieval completed. UserId={OwnerUserId}, DocumentId={DocumentId}, CandidateChunks={CandidateChunks}, VectorSearchHits={VectorSearchHits}, ReturnedChunks={ReturnedChunks}",
            ownerUserId,
            documentId ?? "(all)",
            persistedChunks.Count,
            vectorChunks.Count,
            thresholdedChunks.Count);

        if (thresholdedChunks.Count > 0)
            return thresholdedChunks.Select(x => x.Chunk).ToList();

        var vectorFallbackChunks = vectorChunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .Where(chunk => ChunkAllowsRole(chunk, currentUserRole, sharedAdminDocuments))
            .Take(Math.Clamp(topK, 1, 25))
            .ToList();

        if (vectorFallbackChunks.Count > 0)
            return vectorFallbackChunks;

        if (string.IsNullOrWhiteSpace(documentId) && rankedChunks.Count > 0)
        {
            _logger.LogInformation(
                "Shared/global retrieval using best available semantic fallback chunks below local threshold. UserId={OwnerUserId}, CandidateChunks={CandidateChunks}, ReturnedChunks={ReturnedChunks}, TopScore={TopScore:F4}",
                ownerUserId,
                persistedChunks.Count,
                rankedChunks.Count,
                topScore);

            return rankedChunks.Select(x => x.Chunk).ToList();
        }

        return [];
    }

    private async Task<IReadOnlyList<DocumentChunk>> LoadPersistedChunksAsync(
        string ownerUserId,
        string currentUserRole,
        string? documentId,
        IReadOnlyList<ExistingDocumentRecord> sharedAdminDocuments,
        int? maxChunks,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(documentId))
            {
                var documentChunks = await _chunkRepository.GetChunksByDocumentIdAsync(ownerUserId, documentId, cancellationToken);
                var parentDocuments = sharedAdminDocuments.Count > 0
                    ? sharedAdminDocuments
                    : await LoadParentDocumentScopeAsync(documentId, cancellationToken);

                return FilterChunksByRole(
                    documentChunks,
                    parentDocuments,
                    currentUserRole,
                    ownerUserId,
                    documentId);
            }

            var sharedDocumentIds = sharedAdminDocuments
                .Select(document => document.DocumentId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sharedDocumentIds.Count == 0)
                return [];

            var sharedChunks = await _chunkRepository.GetChunksByDocumentsAsync(
                sharedDocumentIds,
                cancellationToken,
                maxChunks);

            _logger.LogInformation(
                "DynamoDB shared fallback chunks loaded. CurrentUserId={OwnerUserId}, CurrentUserRole={CurrentUserRole}, SharedAdminDocumentCount={SharedAdminDocumentCount}, SharedOwnerUserIds={SharedOwnerUserIds}, FallbackChunkCount={FallbackChunkCount}",
                ownerUserId,
                currentUserRole,
                sharedAdminDocuments.Count,
                string.Join(",", sharedAdminDocuments.Select(document => document.OwnerUserId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase)),
                sharedChunks.Count);

            return FilterChunksByRole(
                sharedChunks,
                sharedAdminDocuments,
                currentUserRole,
                ownerUserId,
                documentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "DynamoDB chunks could not be loaded. UserId={OwnerUserId}, DocumentId={DocumentId}",
                ownerUserId,
                documentId ?? "(all)");

            return [];
        }
    }

    private async Task<IReadOnlyList<ExistingDocumentRecord>> GetSearchableAdminDocumentsAsync(
        string currentUserRole,
        CancellationToken cancellationToken)
    {
        var documents = await _documentRepository.GetAdminDocumentsAsync(cancellationToken);

        return documents
            .Where(document => document.IsAdminDocument && document.IsSearchable)
            .Where(document => DocumentAllowsRole(document, currentUserRole))
            .DistinctBy(document => document.DocumentId)
            .ToList();
    }

    private static string BuildChatCacheKey(
        string ownerUserId,
        string currentUserRole,
        string? documentId,
        string outputFormat,
        string normalizedQuestion)
    {
        var scope = string.IsNullOrWhiteSpace(documentId)
            ? "shared-enterprise"
            : $"document:{documentId}";

        var rawKey =
            $"{ChatCacheVersion}|{ownerUserId}|role:{currentUserRole}|{scope}|{outputFormat}|{normalizedQuestion}";

        return "chat:" + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(rawKey)));
    }

    private static bool DocumentAllowsRole(
        ExistingDocumentRecord document,
        string currentUserRole)
    {
        var normalizedCurrentRole = NormalizeRoleForComparison(currentUserRole);

        return NormalizeRolesForComparison(document.AllowedRoles)
            .Contains(normalizedCurrentRole, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ChunkAllowsRole(
        DocumentChunk chunk,
        string currentUserRole,
        IReadOnlyList<ExistingDocumentRecord> parentDocuments)
    {
        var chunkRoles = NormalizeRolesForComparison(chunk.AllowedRoles);

        if (chunkRoles.Count > 0)
        {
            return chunkRoles.Contains(
                NormalizeRoleForComparison(currentUserRole),
                StringComparer.OrdinalIgnoreCase);
        }

        var parentDocument = parentDocuments.FirstOrDefault(document =>
            string.Equals(document.DocumentId, chunk.DocumentId, StringComparison.OrdinalIgnoreCase));

        return parentDocument is not null &&
               DocumentAllowsRole(parentDocument, currentUserRole);
    }

    private async Task<IReadOnlyList<ExistingDocumentRecord>> LoadParentDocumentScopeAsync(
        string documentId,
        CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetDocumentByIdAsync(
            documentId,
            cancellationToken);

        return document is null
            ? []
            : [document];
    }

    private IReadOnlyList<DocumentChunk> FilterChunksByRole(
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ExistingDocumentRecord> parentDocuments,
        string currentUserRole,
        string ownerUserId,
        string? documentId)
    {
        var chunksBeforeFilter = chunks.Count;
        var filteredChunks = chunks
            .Where(chunk => ChunkAllowsRole(chunk, currentUserRole, parentDocuments))
            .ToList();

        var parentRoles = parentDocuments
            .SelectMany(document => document.AllowedRoles)
            .Select(NormalizeRoleForComparison)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chunkRoles = chunks
            .SelectMany(chunk => chunk.AllowedRoles)
            .Select(NormalizeRoleForComparison)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Role-based chunk filter completed. CurrentUserId={OwnerUserId}, CurrentUserRole={CurrentUserRole}, DocumentId={DocumentId}, ParentDocumentAllowedRoles={ParentDocumentAllowedRoles}, ChunkAllowedRoles={ChunkAllowedRoles}, ChunksBeforeRoleFilter={ChunksBeforeRoleFilter}, ChunksAfterRoleFilter={ChunksAfterRoleFilter}, RejectedChunkCount={RejectedChunkCount}",
            ownerUserId,
            NormalizeRoleForComparison(currentUserRole),
            documentId ?? "(all)",
            string.Join(",", parentRoles),
            string.Join(",", chunkRoles),
            chunksBeforeFilter,
            filteredChunks.Count,
            chunksBeforeFilter - filteredChunks.Count);

        return filteredChunks;
    }

    private static List<string> NormalizeRolesForComparison(IEnumerable<string>? roles)
    {
        return (roles ?? [])
            .Select(NormalizeRoleForComparison)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeRoleForComparison(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return string.Empty;

        return EnterpriseRoles.IsValid(role)
            ? EnterpriseRoles.Normalize(role)
            : role.Trim();
    }

    private static string BuildRetrievalQuery(
        string question,
        IReadOnlyList<ConversationMessage> conversationHistory)
    {
        var recentMessages = conversationHistory
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(4)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => $"{x.Role}: {TrimForRetrieval(x.Content, 500)}");

        var historyContext = string.Join(Environment.NewLine, recentMessages);

        if (string.IsNullOrWhiteSpace(historyContext))
            return question;

        return $"{historyContext}{Environment.NewLine}Current question: {question}";
    }

    private static string PrepareSemanticQuery(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return string.Empty;

        var normalized = NormalizeForSearch(question);

        var terms = Tokenize(normalized);

        return string.Join(" ", terms);
    }

    private static bool IsDocumentOverviewRequest(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return false;

        var normalized = NormalizeForSearch(question);

        return normalized.Contains("summarize") ||
               normalized.Contains("summary") ||
               normalized.Contains("overview") ||
               normalized.Contains("give overview") ||
               normalized.Contains("explain this document") ||
               normalized.Contains("explain the document") ||
               normalized.Contains("explain this file") ||
               normalized.Contains("explain the file") ||
               normalized.Contains("explain this pdf") ||
               normalized.Contains("what is this document about") ||
               normalized.Contains("what is the document about") ||
               normalized.Contains("what is this file about");
    }

    private static bool IsGroundedRefusal(string answer)
    {
        return answer.Contains(
            GroundedNoResultMessage,
            StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "could not find relevant information",
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "not found in the uploaded document",
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "not found in the retrieved context",
                StringComparison.OrdinalIgnoreCase);
    }

    private static List<Citation> BuildCitationsFromAnswer(
        string answer,
        IReadOnlyList<DocumentChunk> relevantChunks,
        bool allowContextCitationFallback)
    {
        var citedIndexes = Regex.Matches(
                answer,
                @"\[doc(?<index>\d+)\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => int.TryParse(match.Groups["index"].Value, out var index) ? index - 1 : -1)
            .Where(index => index >= 0 && index < relevantChunks.Count)
            .Distinct()
            .ToList();

        if (citedIndexes.Count == 0 && !allowContextCitationFallback)
            return [];

        var citationChunks = citedIndexes.Count > 0
            ? citedIndexes.Select(index => relevantChunks[index])
            : relevantChunks;

        return citationChunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.FileName))
            .DistinctBy(chunk => $"{chunk.FileName}|{chunk.PageNumber}")
            .Take(5)
            .Select(chunk => new Citation
            {
                FileName = chunk.FileName,
                PageNumber = chunk.PageNumber
            })
            .ToList();
    }

    private static string CleanInternalSourceMarkers(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return answer;

        var cleaned = Regex.Replace(
            answer,
            @"\s*\[doc\d+\]",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        cleaned = Regex.Replace(
            cleaned,
            @"[ \t]{2,}",
            " ",
            RegexOptions.CultureInvariant);

        cleaned = Regex.Replace(
            cleaned,
            @"\s+([,.;:!?])",
            "$1",
            RegexOptions.CultureInvariant);

        return cleaned.Trim();
    }

    private static IReadOnlyList<ExistingDocumentRecord> SelectFallbackDocuments(
        IReadOnlyList<ExistingDocumentRecord> sharedAdminDocuments,
        KeywordProfile keywordProfile)
    {
        if (sharedAdminDocuments.Count == 0)
            return [];

        var selected = new List<ExistingDocumentRecord>();
        var estimatedChunks = 0;

        var rankedDocuments = sharedAdminDocuments
            .Where(document => document.IsAdminDocument && document.IsSearchable)
            .DistinctBy(document => document.DocumentId)
            .Select(document => new
            {
                Document = document,
                KeywordScore = ComputeDocumentKeywordScore(keywordProfile, document)
            })
            .OrderByDescending(x => x.KeywordScore)
            .ThenByDescending(x => x.Document.UpdatedAtUtc)
            .ThenByDescending(x => x.Document.CreatedAtUtc)
            .ToList();

        foreach (var rankedDocument in rankedDocuments)
        {
            if (selected.Count >= MaxFallbackDocuments)
                break;

            selected.Add(rankedDocument.Document);

            var documentChunkCount = rankedDocument.Document.ChunkCount > 0
                ? rankedDocument.Document.ChunkCount
                : 50;
            estimatedChunks += documentChunkCount;

            if (estimatedChunks >= MaxFallbackCandidateChunks)
                break;
        }

        return selected;
    }

    private static double ComputeDocumentKeywordScore(
        KeywordProfile keywordProfile,
        ExistingDocumentRecord document)
    {
        if (keywordProfile.Terms.Count == 0)
            return 0;

        var metadata = NormalizeForSearch($"{document.FileName} {document.StorageKey}");
        if (string.IsNullOrWhiteSpace(metadata))
            return 0;

        double score = 0;

        if (!string.IsNullOrWhiteSpace(keywordProfile.NormalizedQuery) &&
            metadata.Contains(keywordProfile.NormalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.0;
        }

        var metadataTerms = Tokenize(metadata).ToHashSet(StringComparer.OrdinalIgnoreCase);
        score += (double)keywordProfile.Terms.Count(term => metadataTerms.Contains(term)) / keywordProfile.Terms.Count;

        foreach (var phrase in keywordProfile.Phrases)
        {
            if (metadata.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                score += 0.5;
        }

        return score;
    }

    private static KeywordProfile BuildKeywordProfile(string question)
    {
        var query = NormalizeForSearch(question);
        var terms = Tokenize(query);
        var phrases = BuildQueryPhrases(terms).ToList();

        return new KeywordProfile(query, terms, phrases);
    }

    private static double ComputeKeywordScore(KeywordProfile keywordProfile, DocumentChunk chunk)
    {
        var text = NormalizeForSearch(chunk.Text);
        var heading = NormalizeForSearch(string.IsNullOrWhiteSpace(chunk.Heading) ? chunk.Section : chunk.Heading);

        if (string.IsNullOrWhiteSpace(keywordProfile.NormalizedQuery) || string.IsNullOrWhiteSpace(text))
            return 0;

        if (keywordProfile.Terms.Count == 0)
            return 0;

        double score = 0;

        if (text.Contains(keywordProfile.NormalizedQuery, StringComparison.OrdinalIgnoreCase))
            score += 1.2;

        if (!string.IsNullOrWhiteSpace(heading) &&
            heading.Contains(keywordProfile.NormalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.0;
        }

        var headingTerms = Tokenize(heading).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textTerms = Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var textMatches = keywordProfile.Terms.Count(term => textTerms.Contains(term));
        var headingMatches = keywordProfile.Terms.Count(term => headingTerms.Contains(term));

        score += (double)textMatches / keywordProfile.Terms.Count;
        score += headingMatches * 0.55;

        foreach (var phrase in keywordProfile.Phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                score += 0.35;

            if (heading.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                score += 0.85;
        }

        return Math.Min(score, 4.0) / 4.0;
    }

    private static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static List<string> Tokenize(string value)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "about", "explain", "tell", "me", "the", "to", "of", "in", "on", "for", "is", "what", "section", "summarize"
        };

        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !stopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> BuildQueryPhrases(IReadOnlyList<string> terms)
    {
        for (var i = 0; i < terms.Count - 1; i++)
            yield return $"{terms[i]} {terms[i + 1]}";

        for (var i = 0; i < terms.Count - 2; i++)
            yield return $"{terms[i]} {terms[i + 1]} {terms[i + 2]}";
    }

    private static string BuildCitationSnippet(string text)
    {
        var normalized = string.Join(
            " ",
            text.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= 260
            ? normalized
            : normalized[..260] + "...";
    }

    private static string TrimForRetrieval(string input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input.Length <= maxLength
            ? input
            : input[..maxLength] + "...";
    }

    private static double Magnitude(IReadOnlyList<float> values)
    {
        double sum = 0;

        for (var i = 0; i < values.Count; i++)
            sum += values[i] * values[i];

        return Math.Sqrt(sum);
    }

    private static double CosineSimilarity(
        IReadOnlyList<float> left,
        double leftMagnitude,
        IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
            return 0;

        double dot = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
            return 0;

        return dot / (leftMagnitude * Math.Sqrt(rightMagnitude));
    }
}
