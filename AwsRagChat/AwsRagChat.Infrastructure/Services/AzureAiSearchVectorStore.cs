using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Services;

public sealed class AzureAiSearchVectorStore : IVectorStore
{
    private readonly SearchClient _searchClient;
    private readonly SearchIndexClient _indexClient;
    private readonly AzureAiSearchOptions _options;
    private readonly ILogger<AzureAiSearchVectorStore> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public AzureAiSearchVectorStore(
        IOptions<AzureAiSearchOptions> options,
        ILogger<AzureAiSearchVectorStore> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _options = options.Value;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("AzureAiSearchPipeline");

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new InvalidOperationException("Azure AI Search Endpoint is missing.");

        if (string.IsNullOrWhiteSpace(_options.IndexName))
            throw new InvalidOperationException("Azure AI Search Index Name is missing.");

        var clientOptions = new SearchClientOptions();
        clientOptions.Retry.MaxRetries = 0; // Disable SDK native retry so Polly handles it

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            var credential = new AzureKeyCredential(_options.ApiKey);
            _indexClient = new SearchIndexClient(new Uri(_options.Endpoint), credential, clientOptions);
        }
        else
        {
            var credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(new Uri(_options.Endpoint), credential, clientOptions);
        }

        _searchClient = _indexClient.GetSearchClient(_options.IndexName);
    }

    private async Task EnsureIndexExistsAsync()
    {
        if (_isInitialized)
            return;

        await _initializeSemaphore.WaitAsync();
        try
        {
            if (_isInitialized)
                return;

            try
            {
                await _resiliencePipeline.ExecuteAsync(async token => await _indexClient.GetIndexAsync(_options.IndexName, token));
                _isInitialized = true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation("Creating Azure AI Search index '{IndexName}' with dimension {Dimension}...", _options.IndexName, _options.VectorDimension);

                var index = new SearchIndex(_options.IndexName)
                {
                    Fields =
                    {
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                        new SimpleField("ownerUserId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("documentId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("chunkId", SearchFieldDataType.String) { IsFilterable = true },
                        new SearchableField("fileName") { IsFilterable = true },
                        new SimpleField("s3Key", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("pageNumber", SearchFieldDataType.Int32) { IsFilterable = true },
                        new SearchableField("section") { IsFilterable = true },
                        new SearchableField("heading") { IsFilterable = true },
                        new SimpleField("chunkOrder", SearchFieldDataType.Int32) { IsFilterable = true },
                        new SearchableField("text") { IsFilterable = true },
                        new SimpleField("isAdminDocument", SearchFieldDataType.Boolean) { IsFilterable = true },
                        new SearchField("allowedRoles", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
                        new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                        {
                            IsSearchable = true,
                            VectorSearchDimensions = _options.VectorDimension,
                            VectorSearchProfileName = "my-vector-profile"
                        },
                        new SimpleField("createdAtUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true }
                    },
                    VectorSearch = new VectorSearch
                    {
                        Profiles =
                        {
                            new VectorSearchProfile("my-vector-profile", "my-hnsw-config")
                        },
                        Algorithms =
                        {
                            new HnswAlgorithmConfiguration("my-hnsw-config")
                            {
                                Parameters = new HnswParameters
                                {
                                    Metric = VectorSearchAlgorithmMetric.Cosine
                                }
                            }
                        }
                    }
                };

                await _resiliencePipeline.ExecuteAsync(async token => await _indexClient.CreateIndexAsync(index, token));
                _isInitialized = true;
                _logger.LogInformation("Successfully created Azure AI Search index '{IndexName}'.", _options.IndexName);
            }
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task IndexDocumentAsync(DocumentChunk chunk)
    {
        if (chunk is null)
            throw new ArgumentNullException(nameof(chunk));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "AzureAiSearch.IndexDocument",
            ActivityKind.Internal);
        activity?.SetTag("search.index", _options.IndexName);
        if (chunk != null)
        {
            activity?.SetTag("document.id", chunk.DocumentId);
            activity?.SetTag("chunk.id", chunk.ChunkId);
        }

        if (chunk is null)
            throw new ArgumentNullException(nameof(chunk));


        if (string.IsNullOrWhiteSpace(chunk.OwnerUserId))
            throw new InvalidOperationException("Chunk OwnerUserId is required before indexing.");

        if (string.IsNullOrWhiteSpace(chunk.DocumentId))
            throw new InvalidOperationException("Chunk DocumentId is required before indexing.");

        if (string.IsNullOrWhiteSpace(chunk.ChunkId))
            throw new InvalidOperationException("ChunkId is required before indexing.");

        if (string.IsNullOrWhiteSpace(chunk.Text))
            throw new InvalidOperationException("Chunk text is required before indexing.");

        if (chunk.Embedding is null || chunk.Embedding.Count == 0)
            throw new InvalidOperationException("Chunk embedding is required before indexing.");

        await EnsureIndexExistsAsync();

        var sanitizedKey = $"{chunk.OwnerUserId}_{chunk.DocumentId}_{chunk.ChunkId}"
            .Replace(":", "_");

        var document = new Dictionary<string, object>
        {
            ["id"] = sanitizedKey,
            ["ownerUserId"] = chunk.OwnerUserId,
            ["documentId"] = chunk.DocumentId,
            ["chunkId"] = chunk.ChunkId,
            ["fileName"] = chunk.FileName,
            ["s3Key"] = chunk.StorageKey,
            ["pageNumber"] = chunk.PageNumber,
            ["section"] = chunk.Section,
            ["heading"] = chunk.Heading,
            ["chunkOrder"] = chunk.ChunkOrder,
            ["text"] = chunk.Text,
            ["isAdminDocument"] = chunk.IsAdminDocument,
            ["allowedRoles"] = chunk.AllowedRoles.ToArray(),
            ["embedding"] = chunk.Embedding.ToArray(),
            ["createdAtUtc"] = chunk.CreatedAtUtc
        };

        var stopwatch = Stopwatch.StartNew();
        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _searchClient.UploadDocumentsAsync(new[] { document }, cancellationToken: token));
        stopwatch.Stop();

        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
            stopwatch.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("operation", "IndexDocument"),
            new KeyValuePair<string, object?>("database", "AzureAiSearch"));

        if (response.Value.Results.Any(r => !r.Succeeded))
        {
            var firstError = response.Value.Results.First(r => !r.Succeeded);
            throw new InvalidOperationException(
                $"Azure AI Search indexing failed for document key {firstError.Key}: {firstError.ErrorMessage} (Status: {firstError.Status})");
        }

        _logger.LogInformation(
            "Indexed chunk into Azure AI Search. Index={IndexName}, UserId={OwnerUserId}, DocumentId={DocumentId}, ChunkId={ChunkId}",
            _options.IndexName,
            chunk.OwnerUserId,
            chunk.DocumentId,
            chunk.ChunkId);
    }

    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerUserId,
        string? documentId,
        IReadOnlyList<float> queryEmbedding,
        int topK = 5,
        bool searchSharedAdminDocuments = false,
        IReadOnlyList<string>? sharedDocumentIds = null,
        string? currentUserRole = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "AzureAiSearch.Search",
            ActivityKind.Internal);
        activity?.SetTag("search.index", _options.IndexName);
        activity?.SetTag("search.user", ownerUserId);
        activity?.SetTag("search.topk", topK);

        if (queryEmbedding is null || queryEmbedding.Count == 0)
            throw new ArgumentException("Query embedding is required.", nameof(queryEmbedding));

        if (string.IsNullOrWhiteSpace(currentUserRole))
            throw new ArgumentException("Current user role is required.", nameof(currentUserRole));

        await EnsureIndexExistsAsync();

        _logger.LogInformation(
            "Azure AI Search started. Index={IndexName}, UserId={OwnerUserId}, Role={CurrentUserRole}, DocumentId={DocumentId}, SharedAdminScope={SharedAdminScope}, SharedDocumentCount={SharedDocumentCount}, TopK={TopK}, Dimensions={Dimensions}",
            _options.IndexName,
            ownerUserId,
            currentUserRole,
            documentId ?? "(all)",
            searchSharedAdminDocuments,
            sharedDocumentIds?.Count ?? 0,
            topK,
            queryEmbedding.Count);

        var normalizedTopK = Math.Clamp(topK, 1, 25);
        var normalizedSharedDocumentIds = sharedDocumentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var filterBuilder = new List<string>();

        // Scope documents
        if (searchSharedAdminDocuments)
        {
            filterBuilder.Add("isAdminDocument eq true");
            if (normalizedSharedDocumentIds.Length > 0)
            {
                var docIdsCsv = string.Join(",", normalizedSharedDocumentIds.Select(id => id.Replace("'", "''")));
                filterBuilder.Add($"search.in(documentId, '{docIdsCsv}', ',')");
            }
        }
        else
        {
            filterBuilder.Add($"(ownerUserId eq '{ownerUserId.Replace("'", "''")}' or isAdminDocument eq true)");
        }

        // Apply document ID filter if specifically targeted
        if (!string.IsNullOrWhiteSpace(documentId))
        {
            filterBuilder.Add($"documentId eq '{documentId.Replace("'", "''")}'");
        }

        // Apply role permissions
        var rolesList = BuildRoleVariants(currentUserRole);
        if (rolesList.Count > 0)
        {
            var rolesCsv = string.Join(",", rolesList.Select(r => r.Replace("'", "''")));
            filterBuilder.Add($"allowedRoles/any(r: search.in(r, '{rolesCsv}', ','))");
        }

        var odataFilter = string.Join(" and ", filterBuilder);

        var searchOptions = new SearchOptions
        {
            Filter = odataFilter,
            Size = normalizedTopK
        };

        searchOptions.VectorSearch = new VectorSearchOptions();
        searchOptions.VectorSearch.Queries.Add(new VectorizedQuery(queryEmbedding.ToArray())
        {
            KNearestNeighborsCount = normalizedTopK,
            Fields = { "embedding" }
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _searchClient.SearchAsync<SearchDocument>(null, searchOptions, token),
            cancellationToken);
        stopwatch.Stop();

        var results = new List<DocumentChunk>();
        await foreach (SearchResult<SearchDocument> result in response.Value.GetResultsAsync())
        {
            var doc = result.Document;
            results.Add(new DocumentChunk
            {
                OwnerUserId = GetString(doc, "ownerUserId"),
                DocumentId = GetString(doc, "documentId"),
                ChunkId = GetString(doc, "chunkId"),
                FileName = GetString(doc, "fileName"),
                StorageKey = GetString(doc, "s3Key"),
                PageNumber = GetInt(doc, "pageNumber"),
                Section = GetString(doc, "section"),
                Heading = GetString(doc, "heading"),
                ChunkOrder = GetInt(doc, "chunkOrder"),
                Text = GetString(doc, "text"),
                IsAdminDocument = GetBool(doc, "isAdminDocument"),
                AllowedRoles = GetStringList(doc, "allowedRoles"),
                CreatedAtUtc = GetDateTime(doc, "createdAtUtc")
            });
        }

        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
            stopwatch.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("operation", "Search"),
            new KeyValuePair<string, object?>("database", "AzureAiSearch"));

        _logger.LogInformation(
            "Azure AI Search completed. Index={IndexName}, UserId={OwnerUserId}, Role={CurrentUserRole}, DocumentId={DocumentId}, SharedAdminScope={SharedAdminScope}, SharedDocumentCount={SharedDocumentCount}, ResultCount={ResultCount}, DurationMs={DurationMs}",
            _options.IndexName,
            ownerUserId,
            currentUserRole,
            documentId ?? "(all)",
            searchSharedAdminDocuments,
            normalizedSharedDocumentIds.Length,
            results.Count,
            stopwatch.ElapsedMilliseconds);

        return results;
    }

    private static IReadOnlyList<string> BuildRoleVariants(string currentUserRole)
    {
        if (string.IsNullOrWhiteSpace(currentUserRole))
            return [];

        var trimmed = currentUserRole.Trim();
        var variants = new[]
            {
                trimmed,
                trimmed.ToUpperInvariant(),
                trimmed.ToLowerInvariant(),
                string.Concat(
                    trimmed[..1].ToUpperInvariant(),
                    trimmed.Length > 1
                        ? trimmed[1..].ToLowerInvariant()
                        : string.Empty)
            }
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return variants;
    }

    private static string GetString(SearchDocument doc, string key)
    {
        return doc.TryGetValue(key, out var val) ? val?.ToString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var val) || val is null) return 0;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static bool GetBool(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var val) || val is null) return false;
        if (val is bool b) return b;
        if (bool.TryParse(val.ToString(), out var parsed)) return parsed;
        return false;
    }

    private static DateTime GetDateTime(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var val) || val is null) return DateTime.UtcNow;
        if (val is DateTimeOffset dto) return dto.UtcDateTime;
        if (val is DateTime dt) return dt.ToUniversalTime();
        if (DateTime.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
        return DateTime.UtcNow;
    }

    private static List<string> GetStringList(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var val) || val is null) return [];
        if (val is string[] arr) return arr.ToList();
        if (val is IEnumerable<object> list) return list.Select(o => o?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (val is string str) return str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return [];
    }
}
