using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class CosmosDbDocumentStatusService : IDocumentStatusService
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosDbOptions _options;
    private Container _container = null!;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public CosmosDbDocumentStatusService(
        CosmosClient cosmosClient,
        IOptions<CosmosDbOptions> options)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
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

            var dbResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, cancellationToken: cancellationToken);
            var containerResponse = await dbResponse.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(_options.DocumentsContainer, "/id"),
                cancellationToken: cancellationToken);

            _container = containerResponse.Container;
            _isInitialized = true;
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task MarkUploadedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "UPLOADED",
            null,
            null,
            null,
            null,
            cancellationToken);
    }

    public async Task MarkProcessingAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "PROCESSING",
            null,
            "Document processing started.",
            null,
            null,
            cancellationToken);
    }

    public async Task MarkOcrStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string textractJobId,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "OCR_STARTED",
            textractJobId,
            "Textract OCR job started.",
            null,
            null,
            cancellationToken);
    }

    public async Task MarkOcrCompletedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string textractJobId,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "OCR_COMPLETED",
            textractJobId,
            $"OCR completed. Pages: {pageCount}. Chunks created: {chunkCount}",
            chunkCount,
            pageCount,
            cancellationToken);
    }

    public async Task MarkEmbeddingStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "EMBEDDING",
            null,
            "Generating embeddings.",
            null,
            null,
            cancellationToken);
    }

    public async Task MarkIndexingStartedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "INDEXING",
            null,
            "Indexing chunks into vector store.",
            null,
            null,
            cancellationToken);
    }

    public async Task MarkIndexedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "INDEXED",
            null,
            $"Document indexed successfully. Pages: {pageCount}. Total chunks: {chunkCount}",
            chunkCount,
            pageCount,
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            ownerUserId,
            fileName,
            storageKey,
            "FAILED",
            null,
            errorMessage,
            null,
            null,
            cancellationToken);
    }

    public async Task<bool> GetIsAdminDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT VALUE c.isAdminDocument FROM c WHERE c.id = @documentId")
            .WithParameter("@documentId", documentId);

        var iterator = _container.GetItemQueryIterator<bool>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(documentId) });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            return response.FirstOrDefault();
        }

        return false;
    }

    public async Task<List<string>> GetAllowedRolesAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT VALUE c.allowedRoles FROM c WHERE c.id = @documentId")
            .WithParameter("@documentId", documentId);

        var iterator = _container.GetItemQueryIterator<List<string>>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(documentId) });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            return response.FirstOrDefault() ?? [];
        }

        return [];
    }

    private async Task UpdateStatusAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string status,
        string? textractJobId,
        string? message,
        int? chunkCount,
        int? pageCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Status is required.", nameof(status));

        await EnsureInitializedAsync(cancellationToken);

        CosmosDocumentModel? existing = null;
        try
        {
            var response = await _container.ReadItemAsync<CosmosDocumentModel>(
                documentId,
                new PartitionKey(documentId),
                cancellationToken: cancellationToken);
            existing = response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // document does not exist yet
        }

        if (existing == null)
        {
            var now = DateTime.UtcNow;
            var model = new CosmosDocumentModel
            {
                Id = documentId,
                OwnerUserId = ownerUserId,
                FileName = fileName,
                StorageKey = storageKey,
                Status = status,
                TextractJobId = textractJobId ?? string.Empty,
                Message = message ?? string.Empty,
                ChunkCount = chunkCount ?? 0,
                PageCount = pageCount ?? 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsAdminDocument = false,
                AllowedRoles = []
            };

            await _container.CreateItemAsync(model, new PartitionKey(documentId), cancellationToken: cancellationToken);
        }
        else
        {
            var patches = new List<PatchOperation>
            {
                PatchOperation.Set("/status", status),
                PatchOperation.Set("/ownerUserId", ownerUserId),
                PatchOperation.Set("/fileName", fileName),
                PatchOperation.Set("/storageKey", storageKey),
                PatchOperation.Set("/updatedAtUtc", DateTime.UtcNow)
            };

            if (textractJobId != null)
                patches.Add(PatchOperation.Set("/textractJobId", textractJobId));

            if (message != null)
                patches.Add(PatchOperation.Set("/message", message));

            if (chunkCount.HasValue)
                patches.Add(PatchOperation.Set("/chunkCount", chunkCount.Value));

            if (pageCount.HasValue)
                patches.Add(PatchOperation.Set("/pageCount", pageCount.Value));

            await _container.PatchItemAsync<CosmosDocumentModel>(
                documentId,
                new PartitionKey(documentId),
                patches,
                cancellationToken: cancellationToken);
        }
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

        [JsonProperty("textractJobId")]
        public string TextractJobId { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

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
