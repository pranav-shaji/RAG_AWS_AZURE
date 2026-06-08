using Amazon;
using Amazon.Runtime;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using Microsoft.Extensions.Configuration;
using OpenSearch.Client;
using OpenSearch.Net;
using OpenSearch.Net.Auth.AwsSigV4;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Services;

public sealed class OpenSearchService : IVectorStore
{
    private readonly IOpenSearchClient _client;
    private readonly string _indexName;
    private readonly ILogger<OpenSearchService>? _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public OpenSearchService(
        IConfiguration config,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<OpenSearchService>? logger = null)
    {
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("OpenSearchPipeline");

        var endpoint = config["VectorStore:Endpoint"] ?? config["OpenSearch:Endpoint"];
        var region = config["AWS:Region"] ?? "us-east-1";
        _indexName = config["VectorStore:IndexName"] ?? config["OpenSearch:IndexName"] ?? "rag-index";

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("VectorStore / OpenSearch endpoint is missing.");

        var regionEndpoint = RegionEndpoint.GetBySystemName(region);

#pragma warning disable CS0618
        var credentials = FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618

        var awsConnection = new AwsSigV4HttpConnection(
            credentials,
            regionEndpoint,
            service: "aoss");

        var pool = new SingleNodeConnectionPool(new Uri(endpoint));

        var settings = new ConnectionSettings(pool, awsConnection)
            .ThrowExceptions(true)
            .DefaultIndex(_indexName);

        _client = new OpenSearchClient(settings);
    }

    public async Task IndexDocumentAsync(DocumentChunk chunk)
    {
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

        var document = new
        {
            ownerUserId = chunk.OwnerUserId,
            documentId = chunk.DocumentId,
            chunkId = chunk.ChunkId,
            fileName = chunk.FileName,
            s3Key = chunk.StorageKey,
            pageNumber = chunk.PageNumber,
            section = chunk.Section,
            heading = chunk.Heading,
            chunkOrder = chunk.ChunkOrder,
            text = chunk.Text,
            isAdminDocument = chunk.IsAdminDocument,
            allowedRoles = chunk.AllowedRoles.ToArray(),
            embedding = chunk.Embedding.ToArray(),
            createdAtUtc = chunk.CreatedAtUtc
        };

        var response = await _resiliencePipeline.ExecuteAsync(async token =>
            await _client.IndexAsync(document, i => i
                .Index(_indexName)
                .Id($"{chunk.OwnerUserId}:{chunk.DocumentId}:{chunk.ChunkId}")
                .Refresh(Refresh.WaitFor), token));

        if (!response.IsValid)
            throw new InvalidOperationException($"Vector search indexing failed: {response.DebugInformation}");

        _logger?.LogInformation(
            "Indexed chunk into vector store. Index={IndexName}, UserId={OwnerUserId}, DocumentId={DocumentId}, ChunkId={ChunkId}",
            _indexName,
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

        if (queryEmbedding is null || queryEmbedding.Count == 0)
            throw new ArgumentException("Query embedding is required.", nameof(queryEmbedding));

        if (string.IsNullOrWhiteSpace(currentUserRole))
            throw new ArgumentException("Current user role is required.", nameof(currentUserRole));

        _logger?.LogInformation(
            "Vector search started. Index={IndexName}, UserId={OwnerUserId}, Role={CurrentUserRole}, DocumentId={DocumentId}, SharedAdminScope={SharedAdminScope}, SharedDocumentCount={SharedDocumentCount}, TopK={TopK}, Dimensions={Dimensions}",
            _indexName,
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

        var filterClauses = searchSharedAdminDocuments
            ? BuildSharedAdminFilterClauses(normalizedSharedDocumentIds)
            : new List<object>
            {
                new
                {
                    @bool = new
                    {
                        should = new object[]
                        {
                            BuildTermFilter("ownerUserId", ownerUserId),
                            BuildTermFilter("ownerUserId.keyword", ownerUserId),
                            BuildTermFilter("isAdminDocument", true)
                        },
                        minimum_should_match = 1
                    }
                }
            };

        if (!string.IsNullOrWhiteSpace(documentId))
        {
            filterClauses.Add(new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        BuildTermFilter("documentId", documentId),
                        BuildTermFilter("documentId.keyword", documentId)
                    },
                    minimum_should_match = 1
                }
            });
        }

        filterClauses.Add(BuildRoleFilter(currentUserRole));

        var requestBody = new
        {
            size = normalizedTopK,
            _source = new[]
            {
                "ownerUserId",
                "documentId",
                "chunkId",
                "fileName",
                "s3Key",
                "pageNumber",
                "section",
                "heading",
                "chunkOrder",
                "text",
                "isAdminDocument",
                "allowedRoles",
                "createdAtUtc"
            },
            query = new
            {
                knn = new Dictionary<string, object>
                {
                    ["embedding"] = new
                    {
                        vector = queryEmbedding.ToArray(),
                        k = normalizedTopK,
                        filter = new
                        {
                            @bool = new
                            {
                                filter = filterClauses
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);

        var stopwatch = Stopwatch.StartNew();
        var response = await _resiliencePipeline.ExecuteAsync(async token =>
            await _client.LowLevel.SearchAsync<StringResponse>(
                _indexName,
                PostData.String(json),
                null,
                token),
            cancellationToken);
        stopwatch.Stop();

        if (!response.Success)
            throw new InvalidOperationException($"Vector search kNN query failed: {response.DebugInformation}");

        var results = ParseSearchResults(response.Body);

        _logger?.LogInformation(
            "Vector search completed. Index={IndexName}, UserId={OwnerUserId}, Role={CurrentUserRole}, DocumentId={DocumentId}, SharedAdminScope={SharedAdminScope}, SharedDocumentCount={SharedDocumentCount}, ResultCount={ResultCount}, DurationMs={DurationMs}",
            _indexName,
            ownerUserId,
            currentUserRole,
            documentId ?? "(all)",
            searchSharedAdminDocuments,
            normalizedSharedDocumentIds.Length,
            results.Count,
            stopwatch.ElapsedMilliseconds);

        return results;
    }

    private static List<object> BuildSharedAdminFilterClauses(IReadOnlyList<string> sharedDocumentIds)
    {
        if (sharedDocumentIds.Count > 0)
        {
            return
            [
                BuildTermFilter("isAdminDocument", true),
                new
                {
                    @bool = new
                    {
                        should = new object[]
                        {
                            BuildTermsFilter("documentId", sharedDocumentIds),
                            BuildTermsFilter("documentId.keyword", sharedDocumentIds)
                        },
                        minimum_should_match = 1
                    }
                }
            ];
        }

        return
        [
            BuildTermFilter("isAdminDocument", true)
        ];
    }

    private static object BuildTermFilter(string fieldName, object value)
    {
        return new
        {
            term = new Dictionary<string, object>
            {
                [fieldName] = value
            }
        };
    }

    private static object BuildTermsFilter(string fieldName, IReadOnlyList<string> values)
    {
        return new
        {
            terms = new Dictionary<string, object>
            {
                [fieldName] = values
            }
        };
    }

    private static object BuildRoleFilter(string currentUserRole)
    {
        var roleVariants = BuildRoleVariants(currentUserRole);

        return new
        {
            @bool = new
            {
                should = new object[]
                {
                    BuildTermsFilter("allowedRoles", roleVariants),
                    BuildTermsFilter("allowedRoles.keyword", roleVariants)
                },
                minimum_should_match = 1
            }
        };
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

    private static IReadOnlyList<DocumentChunk> ParseSearchResults(string json)
    {
        var results = new List<DocumentChunk>();

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("hits", out var hitsRoot))
            return results;

        if (!hitsRoot.TryGetProperty("hits", out var hitsArray))
            return results;

        foreach (var hit in hitsArray.EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var source))
                continue;

            results.Add(new DocumentChunk
            {
                OwnerUserId = GetString(source, "ownerUserId"),
                DocumentId = GetString(source, "documentId"),
                ChunkId = GetString(source, "chunkId"),
                FileName = GetString(source, "fileName"),
                StorageKey = GetString(source, "s3Key"),
                PageNumber = GetInt(source, "pageNumber"),
                Section = GetString(source, "section"),
                Heading = GetString(source, "heading"),
                ChunkOrder = GetInt(source, "chunkOrder"),
                Text = GetString(source, "text"),
                IsAdminDocument = GetBool(source, "isAdminDocument"),
                AllowedRoles = GetStringList(source, "allowedRoles"),
                CreatedAtUtc = GetDateTime(source, "createdAtUtc")
            });
        }

        return results;
    }

    private static string GetString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static bool GetBool(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static DateTime GetDateTime(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
            return DateTime.UtcNow;

        return DateTime.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
    }

    private static List<string> GetStringList(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return [];
    }
}
