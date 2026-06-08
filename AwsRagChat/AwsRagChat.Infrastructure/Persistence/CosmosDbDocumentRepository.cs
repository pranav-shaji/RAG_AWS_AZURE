using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class CosmosDbDocumentRepository : IDocumentRepository
{
    private const string DefaultTableName = "rag-documents";
    private const string OwnerFileHashIndexName = "OwnerUserId-FileHash-index";

    private readonly CosmosClient _cosmosClient;
    private readonly CosmosDbOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;
    private Container _container = null!;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public CosmosDbDocumentRepository(
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
                    new ContainerProperties(_options.DocumentsContainer, "/id"),
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

    public async Task<ExistingDocumentRecord?> FindByOwnerAndHashAsync(
        string ownerUserId,
        string fileHash,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(fileHash))
            throw new ArgumentException("FileHash is required.", nameof(fileHash));

        var query = new QueryDefinition("SELECT * FROM c WHERE c.ownerUserId = @ownerUserId AND c.fileHash = @fileHash")
            .WithParameter("@ownerUserId", ownerUserId)
            .WithParameter("@fileHash", fileHash);

        var iterator = _container.GetItemQueryIterator<CosmosDocumentModel>(query);

        if (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            var item = response.FirstOrDefault();
            if (item != null)
            {
                return Map(item);
            }
        }

        return null;
    }

    public async Task<ExistingDocumentRecord?> GetDocumentByIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await _container.ReadItemAsync<CosmosDocumentModel>(
                    documentId,
                    new PartitionKey(documentId),
                    cancellationToken: token),
                cancellationToken);

            return Map(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CreateUploadRecordAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string fileHash,
        long fileSizeBytes,
        bool isAdminDocument,
        IReadOnlyList<string> allowedRoles,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var model = new CosmosDocumentModel
        {
            Id = documentId,
            OwnerUserId = ownerUserId,
            FileName = fileName,
            StorageKey = storageKey,
            FileHash = fileHash,
            Status = "UPLOADED",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ChunkCount = 0,
            PageCount = 0,
            IsAdminDocument = isAdminDocument,
            AllowedRoles = allowedRoles.ToList()
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _container.UpsertItemAsync(model, new PartitionKey(documentId), cancellationToken: token),
            cancellationToken);
    }

    public async Task<int> GetDocumentCountAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.ownerUserId = @ownerUserId")
            .WithParameter("@ownerUserId", ownerUserId);

        return await GetScalarAsync<int>(query, cancellationToken);
    }

    public async Task<List<ExistingDocumentRecord>> GetDocumentsByUserAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.ownerUserId = @ownerUserId")
            .WithParameter("@ownerUserId", ownerUserId);

        var iterator = _container.GetItemQueryIterator<CosmosDocumentModel>(query);
        var results = new List<ExistingDocumentRecord>();

        while (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            results.AddRange(response.Select(Map));
        }

        return results;
    }

    public async Task UpdateStatusAsync(
        string documentId,
        string status,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var patchOperations = new[]
        {
            PatchOperation.Set("/status", status),
            PatchOperation.Set("/updatedAtUtc", DateTime.UtcNow)
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _container.PatchItemAsync<CosmosDocumentModel>(
                documentId,
                new PartitionKey(documentId),
                patchOperations,
                cancellationToken: token),
            cancellationToken);
    }

    public async Task MarkIndexedAsync(
        string documentId,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var patchOperations = new[]
        {
            PatchOperation.Set("/status", "INDEXED"),
            PatchOperation.Set("/chunkCount", chunkCount),
            PatchOperation.Set("/pageCount", pageCount),
            PatchOperation.Set("/updatedAtUtc", DateTime.UtcNow)
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _container.PatchItemAsync<CosmosDocumentModel>(
                documentId,
                new PartitionKey(documentId),
                patchOperations,
                cancellationToken: token),
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var patchOperations = new[]
        {
            PatchOperation.Set("/status", "FAILED"),
            PatchOperation.Set("/updatedAtUtc", DateTime.UtcNow)
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _container.PatchItemAsync<CosmosDocumentModel>(
                documentId,
                new PartitionKey(documentId),
                patchOperations,
                cancellationToken: token),
            cancellationToken);
    }

    public async Task<List<ExistingDocumentRecord>> GetRecentDocumentsAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.updatedAtUtc DESC OFFSET 0 LIMIT @take")
            .WithParameter("@take", take);

        var iterator = _container.GetItemQueryIterator<CosmosDocumentModel>(query);
        var results = new List<ExistingDocumentRecord>();

        if (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            results.AddRange(response.Select(Map));
        }

        return results;
    }

    public async Task<PagedResult<ExistingDocumentRecord>> GetDocumentMetadataPageAsync(
        int pageSize = 20,
        string? nextToken = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.updatedAtUtc DESC");
        var iterator = _container.GetItemQueryIterator<CosmosDocumentModel>(
            query,
            continuationToken: nextToken,
            requestOptions: new QueryRequestOptions { MaxItemCount = pageSize });

        if (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            return new PagedResult<ExistingDocumentRecord>
            {
                Items = response.Select(Map).ToList(),
                NextToken = response.ContinuationToken
            };
        }

        return new PagedResult<ExistingDocumentRecord>();
    }

    public async Task<DocumentStatsSnapshot> GetDocumentStatsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var snapshot = new DocumentStatsSnapshot();

        snapshot.TotalDocuments = await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c"), cancellationToken);
        snapshot.IndexedDocuments = await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.status = 'INDEXED'"), cancellationToken);
        snapshot.FailedDocuments = await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.status = 'FAILED'"), cancellationToken);
        snapshot.UploadedDocuments = await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.status = 'UPLOADED'"), cancellationToken);
        snapshot.TotalChunks = await GetScalarAsync<long>(new QueryDefinition("SELECT VALUE SUM(c.chunkCount) FROM c"), cancellationToken);
        snapshot.TotalPages = await GetScalarAsync<long>(new QueryDefinition("SELECT VALUE SUM(c.pageCount) FROM c"), cancellationToken);

        return snapshot;
    }

    public async Task<int> GetTotalDocumentCountAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c"), cancellationToken);
    }

    public async Task<int> GetDocumentCountByStatusAsync(
        string status,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.status = @status")
            .WithParameter("@status", status);

        return await GetScalarAsync<int>(query, cancellationToken);
    }

    public async Task<long> GetTotalChunkCountAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await GetScalarAsync<long>(new QueryDefinition("SELECT VALUE SUM(c.chunkCount) FROM c"), cancellationToken);
    }

    public async Task<long> GetTotalPageCountAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await GetScalarAsync<long>(new QueryDefinition("SELECT VALUE SUM(c.pageCount) FROM c"), cancellationToken);
    }

    public async Task<IReadOnlyList<ExistingDocumentRecord>> GetAdminDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.isAdminDocument = true");
        var iterator = _container.GetItemQueryIterator<CosmosDocumentModel>(query);
        var results = new List<ExistingDocumentRecord>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(response.Select(Map));
        }

        return results;
    }

    private async Task<T> GetScalarAsync<T>(QueryDefinition queryDefinition, CancellationToken cancellationToken)
    {
        var iterator = _container.GetItemQueryIterator<T?>(queryDefinition);
        if (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            var val = response.FirstOrDefault();
            return val ?? default!;
        }
        return default!;
    }

    private static ExistingDocumentRecord Map(CosmosDocumentModel model)
    {
        return new ExistingDocumentRecord
        {
            DocumentId = model.Id,
            OwnerUserId = model.OwnerUserId,
            FileName = model.FileName,
            StorageKey = model.StorageKey,
            FileHash = model.FileHash,
            Status = model.Status,
            CreatedAtUtc = model.CreatedAtUtc,
            UpdatedAtUtc = model.UpdatedAtUtc,
            ChunkCount = model.ChunkCount,
            PageCount = model.PageCount,
            IsAdminDocument = model.IsAdminDocument,
            AllowedRoles = model.AllowedRoles
        };
    }

    private sealed class CosmosDocumentModel
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("ownerUserId")]
        public string OwnerUserId { get; set; } = string.Empty;

        [JsonProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonProperty("storageKey")]
        public string StorageKey { get; set; } = string.Empty;

        [JsonProperty("fileHash")]
        public string FileHash { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; }

        [JsonProperty("chunkCount")]
        public int ChunkCount { get; set; }

        [JsonProperty("pageCount")]
        public int PageCount { get; set; }

        [JsonProperty("isAdminDocument")]
        public bool IsAdminDocument { get; set; }

        [JsonProperty("allowedRoles")]
        public List<string> AllowedRoles { get; set; } = [];
    }
}
