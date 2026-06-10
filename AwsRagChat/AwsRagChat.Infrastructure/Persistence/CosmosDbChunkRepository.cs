using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class CosmosDbChunkRepository : IChunkRepository
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosDbOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;
    private Container _container = null!;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public CosmosDbChunkRepository(
        CosmosClient cosmosClient,
        IOptions<CosmosDbOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
        _resiliencePipeline = pipelineProvider.GetPipeline("CosmosDbPipeline");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized && _container != null)
            return;

        await _initializeSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized && _container != null)
                return;

            var dbResponse = await _resiliencePipeline.ExecuteAsync(
                async token => await _cosmosClient.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, cancellationToken: token),
                cancellationToken);
            var containerResponse = await _resiliencePipeline.ExecuteAsync(
                async token => await dbResponse.Database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(_options.ChunksContainer, "/documentId"),
                    cancellationToken: token),
                cancellationToken);

            _container = containerResponse.Container;
            _isInitialized = true;
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task SaveChunksAsync(
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.SaveChunks",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ChunksContainer);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var chunk in chunks)
            {
                var model = new CosmosChunkModel
                {
                    Id = chunk.ChunkId,
                    DocumentId = chunk.DocumentId,
                    OwnerUserId = chunk.OwnerUserId,
                    FileName = chunk.FileName,
                    StorageKey = chunk.StorageKey,
                    PageNumber = chunk.PageNumber,
                    Section = chunk.Section ?? string.Empty,
                    Heading = chunk.Heading ?? string.Empty,
                    ChunkOrder = chunk.ChunkOrder,
                    Text = chunk.Text,
                    IsAdminDocument = chunk.IsAdminDocument,
                    AllowedRoles = chunk.AllowedRoles.ToList(),
                    CreatedAtUtc = chunk.CreatedAtUtc,
                    Embedding = chunk.Embedding.ToList()
                };

                await _resiliencePipeline.ExecuteAsync(
                    async token => await _container.UpsertItemAsync(model, new PartitionKey(chunk.DocumentId), cancellationToken: token),
                    cancellationToken);
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ChunksContainer),
                new KeyValuePair<string, object?>("operation", "UpsertItems"));
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksByDocumentIdAsync(
        string ownerUserId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetChunksByDocumentId",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ChunksContainer);
        activity?.SetTag("document.id", documentId);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.documentId = @documentId AND (c.ownerUserId = @ownerUserId OR c.isAdminDocument = true)")
                .WithParameter("@documentId", documentId)
                .WithParameter("@ownerUserId", ownerUserId);

            var iterator = _container.GetItemQueryIterator<CosmosChunkModel>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(documentId) });

            var results = new List<DocumentChunk>();
            while (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                results.AddRange(response.Select(Map));
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ChunksContainer),
                new KeyValuePair<string, object?>("operation", "Query"));

            return results
                .OrderBy(c => c.ChunkOrder)
                .ToList();
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetAllChunks",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ChunksContainer);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // This is a cross-partition query since partition key is documentId
            var query = new QueryDefinition("SELECT * FROM c WHERE c.ownerUserId = @ownerUserId OR c.isAdminDocument = true")
                .WithParameter("@ownerUserId", ownerUserId);

            var iterator = _container.GetItemQueryIterator<CosmosChunkModel>(query);
            var results = new List<DocumentChunk>();

            while (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                results.AddRange(response.Select(Map));
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ChunksContainer),
                new KeyValuePair<string, object?>("operation", "Query"));

            return results;
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksByDocumentsAsync(
        IReadOnlyList<string> documentIds,
        CancellationToken cancellationToken = default,
        int? maxChunks = null)
    {
        if (documentIds is null || documentIds.Count == 0)
            return Array.Empty<DocumentChunk>();

        var normalizedDocumentIds = documentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedDocumentIds.Count == 0)
            return Array.Empty<DocumentChunk>();

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetChunksByDocuments",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ChunksContainer);
        activity?.SetTag("documents.count", normalizedDocumentIds.Count);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tasks = normalizedDocumentIds.Select(async docId =>
            {
                var query = new QueryDefinition("SELECT * FROM c WHERE c.documentId = @docId")
                    .WithParameter("@docId", docId);

                var iterator = _container.GetItemQueryIterator<CosmosChunkModel>(
                    query,
                    requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(docId) });

                var chunks = new List<DocumentChunk>();
                while (iterator.HasMoreResults)
                {
                    var response = await _resiliencePipeline.ExecuteAsync(
                        async token => await iterator.ReadNextAsync(token),
                        cancellationToken);
                    chunks.AddRange(response.Select(Map));
                }
                return chunks;
            }).ToList();

            var chunkGroups = await Task.WhenAll(tasks);
            var results = chunkGroups
                .SelectMany(g => g)
                .OrderBy(c => c.DocumentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ChunkOrder)
                .ToList();

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ChunksContainer),
                new KeyValuePair<string, object?>("operation", "QueryMultiple"));

            if (maxChunks.HasValue && maxChunks.Value > 0)
            {
                return results.Take(maxChunks.Value).ToList();
            }

            return results;
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    private static DocumentChunk Map(CosmosChunkModel model)
    {
        return new DocumentChunk
        {
            ChunkId = model.Id,
            DocumentId = model.DocumentId,
            OwnerUserId = model.OwnerUserId,
            FileName = model.FileName,
            StorageKey = model.StorageKey,
            PageNumber = model.PageNumber,
            Section = model.Section,
            Heading = model.Heading,
            ChunkOrder = model.ChunkOrder,
            Text = model.Text,
            IsAdminDocument = model.IsAdminDocument,
            AllowedRoles = model.AllowedRoles,
            CreatedAtUtc = model.CreatedAtUtc,
            Embedding = model.Embedding
        };
    }

    private sealed class CosmosChunkModel
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("documentId")]
        public string DocumentId { get; set; } = string.Empty;

        [JsonProperty("ownerUserId")]
        public string OwnerUserId { get; set; } = string.Empty;

        [JsonProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonProperty("storageKey")]
        public string StorageKey { get; set; } = string.Empty;

        [JsonProperty("pageNumber")]
        public int PageNumber { get; set; }

        [JsonProperty("section")]
        public string Section { get; set; } = string.Empty;

        [JsonProperty("heading")]
        public string Heading { get; set; } = string.Empty;

        [JsonProperty("chunkOrder")]
        public int ChunkOrder { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;

        [JsonProperty("isAdminDocument")]
        public bool IsAdminDocument { get; set; }

        [JsonProperty("allowedRoles")]
        public List<string> AllowedRoles { get; set; } = [];

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("embedding")]
        public List<float> Embedding { get; set; } = [];
    }
}
