using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class DynamoDbChunkRepository : IChunkRepository
{
    private const int DocumentQueryConcurrency = 8;

    private readonly IAmazonDynamoDB _amazonDynamoDb;
    private readonly DynamoDbOptions _dynamoDbOptions;
    private readonly ILogger<DynamoDbChunkRepository>? _logger;

    public DynamoDbChunkRepository(
        IAmazonDynamoDB amazonDynamoDb,
        IOptions<DynamoDbOptions> dynamoDbOptions,
        ILogger<DynamoDbChunkRepository>? logger = null)
    {
        _amazonDynamoDb = amazonDynamoDb;
        _dynamoDbOptions = dynamoDbOptions.Value;
        _logger = logger;
    }

    public async Task SaveChunksAsync(
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        foreach (var chunk in chunks)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["OwnerUserId"] = new AttributeValue { S = chunk.OwnerUserId },
                ["DocumentId"] = new AttributeValue { S = chunk.DocumentId },
                ["ChunkId"] = new AttributeValue { S = chunk.ChunkId },
                ["FileName"] = new AttributeValue { S = chunk.FileName },
                ["S3Key"] = new AttributeValue { S = chunk.S3Key },
                ["PageNumber"] = new AttributeValue { N = chunk.PageNumber.ToString() },
                ["Section"] = new AttributeValue { S = chunk.Section ?? string.Empty },
                ["Heading"] = new AttributeValue { S = chunk.Heading ?? string.Empty },
                ["ChunkOrder"] = new AttributeValue { N = chunk.ChunkOrder.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["Text"] = new AttributeValue { S = chunk.Text },
                ["IsAdminDocument"] = new AttributeValue { BOOL = chunk.IsAdminDocument },
                ["AllowedRoles"] = new AttributeValue
                {
                    SS = chunk.AllowedRoles
                        .Where(role => !string.IsNullOrWhiteSpace(role))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                },
                ["CreatedAtUtc"] = new AttributeValue { S = chunk.CreatedAtUtc.ToString("O") },
                ["Embedding"] = new AttributeValue
                {
                    L = chunk.Embedding.Select(e => new AttributeValue
                    {
                        N = e.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }).ToList()
                }
            };

            var request = new PutItemRequest
            {
                TableName = _dynamoDbOptions.TableName,
                Item = item
            };

            await _amazonDynamoDb.PutItemAsync(request, cancellationToken);
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

        var queriedChunks = await TryQueryChunksByDocumentIdsAsync(
            [documentId],
            cancellationToken,
            maxChunks: null);

        if (queriedChunks is not null)
        {
            return queriedChunks
                .Where(chunk =>
                    string.Equals(chunk.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase) ||
                    chunk.IsAdminDocument)
                .OrderBy(chunk => chunk.ChunkOrder)
                .ToList();
        }

        var request = new ScanRequest
        {
            TableName = _dynamoDbOptions.TableName,
            FilterExpression = "DocumentId = :documentId AND (OwnerUserId = :ownerUserId OR IsAdminDocument = :isAdminDocument)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":documentId"] = new AttributeValue { S = documentId },
                [":ownerUserId"] = new AttributeValue { S = ownerUserId },
                [":isAdminDocument"] = new AttributeValue { BOOL = true }
            }
        };

        var results = new List<DocumentChunk>();

        ScanResponse response;

        do
        {
            response = await _amazonDynamoDb.ScanAsync(
                request,
                cancellationToken);

            results.AddRange(
                response.Items.Select(MapToChunk));

            request.ExclusiveStartKey =
                response.LastEvaluatedKey;

        } while (
            response.LastEvaluatedKey != null &&
            response.LastEvaluatedKey.Count > 0);

        return results;
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(
    string ownerUserId,
    CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var results = new List<DocumentChunk>();

        var request = new ScanRequest
        {
            TableName = _dynamoDbOptions.TableName,
            FilterExpression = "OwnerUserId = :ownerUserId OR IsAdminDocument = :isAdminDocument",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":ownerUserId"] = new AttributeValue { S = ownerUserId },
                [":isAdminDocument"] = new AttributeValue { BOOL = true }
            }
        };

        ScanResponse response;

        do
        {
            response = await _amazonDynamoDb.ScanAsync(
                request,
                cancellationToken);

            results.AddRange(
                response.Items.Select(MapToChunk));

            request.ExclusiveStartKey =
                response.LastEvaluatedKey;

        } while (
            response.LastEvaluatedKey != null &&
            response.LastEvaluatedKey.Count > 0);

        return results;
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksByDocumentsAsync(
        IReadOnlyList<string> documentIds,
        CancellationToken cancellationToken = default,
        int? maxChunks = null)
    {
        if (documentIds is null || documentIds.Count == 0)
            return new List<DocumentChunk>();

        var normalizedDocumentIds = documentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedDocumentIds.Count == 0)
            return new List<DocumentChunk>();

        var queriedChunks = await TryQueryChunksByDocumentIdsAsync(
            normalizedDocumentIds,
            cancellationToken,
            maxChunks);

        if (queriedChunks is not null)
            return queriedChunks;

        var normalizedMaxChunks = maxChunks.GetValueOrDefault();
        var results = new List<DocumentChunk>();
        var request = new ScanRequest
        {
            TableName = _dynamoDbOptions.TableName,
            FilterExpression = "DocumentId IN (" + string.Join(", ", normalizedDocumentIds.Select((_, i) => $":docId{i}")) + ")",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>()
        };

        if (normalizedMaxChunks > 0)
            request.Limit = Math.Min(normalizedMaxChunks, 250);

        for (int i = 0; i < normalizedDocumentIds.Count; i++)
        {
            request.ExpressionAttributeValues[$":docId{i}"] = new AttributeValue { S = normalizedDocumentIds[i] };
        }

        ScanResponse response;

        do
        {
            response = await _amazonDynamoDb.ScanAsync(request, cancellationToken);

            foreach (var item in response.Items)
            {
                results.Add(MapToChunk(item));
            }

            if (normalizedMaxChunks > 0 && results.Count >= normalizedMaxChunks)
                break;

            request.ExclusiveStartKey = response.LastEvaluatedKey;

        } while (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0);

        return normalizedMaxChunks > 0
            ? results.Take(normalizedMaxChunks).ToList()
            : results;
    }

    private async Task<IReadOnlyList<DocumentChunk>?> TryQueryChunksByDocumentIdsAsync(
        IReadOnlyList<string> documentIds,
        CancellationToken cancellationToken,
        int? maxChunks)
    {
        var normalizedMaxChunks = maxChunks.GetValueOrDefault();
        var semaphore = new SemaphoreSlim(DocumentQueryConcurrency);
        var tasks = documentIds.Select(QueryDocumentWithConcurrencyAsync).ToList();

        try
        {
            var chunkGroups = await Task.WhenAll(tasks);

            var chunks = chunkGroups
                .SelectMany(group => group)
                .OrderBy(chunk => chunk.DocumentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(chunk => chunk.ChunkOrder)
                .ToList();

            _logger?.LogInformation(
                "DynamoDB chunk query completed. DocumentCount={DocumentCount}, ChunkCount={ChunkCount}, MaxChunks={MaxChunks}",
                documentIds.Count,
                chunks.Count,
                maxChunks);

            return normalizedMaxChunks > 0
                ? chunks.Take(normalizedMaxChunks).ToList()
                : chunks;
        }
        catch (AmazonDynamoDBException ex) when (IsUnsupportedQueryShape(ex))
        {
            _logger?.LogWarning(
                ex,
                "DynamoDB chunk query is not supported by the current table key schema. Falling back to scan. DocumentCount={DocumentCount}",
                documentIds.Count);

            return null;
        }

        async Task<IReadOnlyList<DocumentChunk>> QueryDocumentWithConcurrencyAsync(string documentId)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                return await QueryChunksByDocumentIdAsync(
                    documentId,
                    cancellationToken,
                    maxChunks);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private async Task<IReadOnlyList<DocumentChunk>> QueryChunksByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken,
        int? maxChunks)
    {
        var normalizedMaxChunks = maxChunks.GetValueOrDefault();
        var results = new List<DocumentChunk>();
        var request = new QueryRequest
        {
            TableName = _dynamoDbOptions.TableName,
            KeyConditionExpression = "DocumentId = :documentId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":documentId"] = new AttributeValue { S = documentId }
            }
        };

        if (normalizedMaxChunks > 0)
            request.Limit = normalizedMaxChunks;

        QueryResponse response;

        do
        {
            response = await _amazonDynamoDb.QueryAsync(
                request,
                cancellationToken);

            results.AddRange(response.Items.Select(MapToChunk));

            if (normalizedMaxChunks > 0 && results.Count >= normalizedMaxChunks)
                break;

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return normalizedMaxChunks > 0
            ? results.Take(normalizedMaxChunks).ToList()
            : results;
    }

    private static bool IsUnsupportedQueryShape(AmazonDynamoDBException ex)
    {
        return string.Equals(ex.ErrorCode, "ValidationException", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Query condition missed key schema element", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Query key condition not supported", StringComparison.OrdinalIgnoreCase);
    }

    private static DocumentChunk MapToChunk(Dictionary<string, AttributeValue> item)
    {
        var chunk = new DocumentChunk
        {
            OwnerUserId = item.TryGetValue("OwnerUserId", out var ownerUserId) ? ownerUserId.S : string.Empty,
            DocumentId = item.TryGetValue("DocumentId", out var documentId) ? documentId.S : string.Empty,
            ChunkId = item.TryGetValue("ChunkId", out var chunkId) ? chunkId.S : string.Empty,
            FileName = item.TryGetValue("FileName", out var fileName) ? fileName.S : string.Empty,
            S3Key = item.TryGetValue("S3Key", out var s3Key) ? s3Key.S : string.Empty,
            PageNumber = item.TryGetValue("PageNumber", out var pageNumber) && int.TryParse(pageNumber.N, out var parsedPage)
                ? parsedPage
                : 0,
            Section = item.TryGetValue("Section", out var section) ? section.S : string.Empty,
            Heading = item.TryGetValue("Heading", out var heading) ? heading.S : string.Empty,
            ChunkOrder = item.TryGetValue("ChunkOrder", out var chunkOrder) && int.TryParse(chunkOrder.N, out var parsedChunkOrder)
                ? parsedChunkOrder
                : 0,
            Text = item.TryGetValue("Text", out var text) ? text.S : string.Empty,
            IsAdminDocument = item.TryGetValue("IsAdminDocument", out var isAdminDocument) && isAdminDocument.BOOL == true,
            AllowedRoles = GetStringList(item, "AllowedRoles"),
            CreatedAtUtc = item.TryGetValue("CreatedAtUtc", out var createdAt) &&
                           DateTime.TryParse(createdAt.S, out var parsedCreatedAt)
                ? parsedCreatedAt
                : DateTime.UtcNow,
            Embedding = item.TryGetValue("Embedding", out var embedding)
                ? embedding.L
                    .Where(x => float.TryParse(
                        x.N,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _))
                    .Select(x => float.Parse(
                        x.N,
                        System.Globalization.CultureInfo.InvariantCulture))
                    .ToList()
                : []
        };

        return chunk;
    }

    private static List<string> GetStringList(
        Dictionary<string, AttributeValue> item,
        string key)
    {
        if (!item.TryGetValue(key, out var value))
            return [];

        if (value.SS is { Count: > 0 })
            return value.SS
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (value.L is { Count: > 0 })
            return value.L
                .Select(attribute => attribute.S ?? string.Empty)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (!string.IsNullOrWhiteSpace(value.S))
            return value.S.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return [];
    }
}
