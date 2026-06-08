using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class DynamoDbDocumentRepository : IDocumentRepository
{
    private const string DefaultTableName = "rag-documents";
    private const string OwnerFileHashIndexName = "OwnerUserId-FileHash-index";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly ResiliencePipeline _resiliencePipeline;

    public DynamoDbDocumentRepository(
        IAmazonDynamoDB dynamoDb,
        IConfiguration configuration,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _dynamoDb = dynamoDb;
        _tableName = configuration["DynamoDb:DocumentsTableName"] ?? DefaultTableName;
        _resiliencePipeline = pipelineProvider.GetPipeline("DynamoDbPipeline");
    }

    public async Task<ExistingDocumentRecord?> FindByOwnerAndHashAsync(
        string ownerUserId,
        string fileHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(fileHash))
            throw new ArgumentException("FileHash is required.", nameof(fileHash));

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = OwnerFileHashIndexName,
                KeyConditionExpression = "OwnerUserId = :ownerUserId AND FileHash = :fileHash",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":ownerUserId"] = new AttributeValue { S = ownerUserId },
                    [":fileHash"] = new AttributeValue { S = fileHash }
                },
                Limit = 1
            }, token), cancellationToken);

        var item = response.Items.FirstOrDefault();

        if (item is null)
            return null;

        return Map(item);
    }

    public async Task<ExistingDocumentRecord?> GetDocumentByIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["DocumentId"] = new AttributeValue { S = documentId }
                }
            }, token), cancellationToken);

        if (response.Item is null || response.Item.Count == 0)
            return null;

        return Map(response.Item);
    }

    public async Task<List<ExistingDocumentRecord>> GetRecentDocumentsAsync(
    int take = 50,
    CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName
        };

        var documents = new List<ExistingDocumentRecord>();
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            documents.AddRange(response.Items.Select(Map));

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return documents
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Max(1, take))
            .ToList();
    }

    public async Task<PagedResult<ExistingDocumentRecord>> GetDocumentMetadataPageAsync(
        int pageSize = 20,
        string? nextToken = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            Limit = Math.Clamp(pageSize, 1, 50),
            ProjectionExpression = "DocumentId, OwnerUserId, FileName, #status, ChunkCount, PageCount, UpdatedAtUtc, CreatedAtUtc, IsAdminDocument, AllowedRoles",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "Status"
            }
        };

        var exclusiveStartKey = DecodeDocumentPageToken(nextToken);

        if (exclusiveStartKey is not null)
            request.ExclusiveStartKey = exclusiveStartKey;

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.ScanAsync(request, token),
            cancellationToken);

        return new PagedResult<ExistingDocumentRecord>
        {
            Items = response.Items
                .Select(Map)
                .OrderByDescending(document => document.UpdatedAtUtc)
                .ToList(),
            NextToken = EncodeDocumentPageToken(response.LastEvaluatedKey)
        };
    }

    public async Task<DocumentStatsSnapshot> GetDocumentStatsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            ProjectionExpression = "#status, ChunkCount, PageCount",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "Status"
            }
        };

        var snapshot = new DocumentStatsSnapshot();
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            foreach (var item in response.Items)
            {
                snapshot.TotalDocuments++;

                var status = GetString(item, "Status");

                if (status.Equals("INDEXED", StringComparison.OrdinalIgnoreCase))
                    snapshot.IndexedDocuments++;
                else if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                    snapshot.FailedDocuments++;
                else if (status.Equals("UPLOADED", StringComparison.OrdinalIgnoreCase))
                    snapshot.UploadedDocuments++;

                snapshot.TotalChunks += GetInt(item, "ChunkCount");
                snapshot.TotalPages += GetInt(item, "PageCount");
            }

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return snapshot;
    }

    public async Task<int> GetTotalDocumentCountAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            Select = Select.COUNT
        };

        var count = 0;
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            count += response.Count ?? 0;

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return count;
    }

    public async Task<List<ExistingDocumentRecord>> GetAccessibleDocumentsAsync(
    string ownerUserId,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(ownerUserId))
        throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

    var request = new ScanRequest
    {
        TableName = _tableName,
        FilterExpression =
            "OwnerUserId = :ownerUserId OR IsAdminDocument = :isAdminDocument",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":ownerUserId"] = new AttributeValue { S = ownerUserId },
            [":isAdminDocument"] = new AttributeValue { BOOL = true }
        }
    };

    var documents = new List<ExistingDocumentRecord>();

    ScanResponse response;

    do
    {
        response = await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.ScanAsync(request, token),
            cancellationToken);

        documents.AddRange(response.Items.Select(Map));

        request.ExclusiveStartKey =
            response.LastEvaluatedKey;
    }
    while (response.LastEvaluatedKey is { Count: > 0 });

    return documents
        .OrderByDescending(x => x.UpdatedAtUtc)
        .ToList();
}

    public async Task<int> GetDocumentCountByStatusAsync(
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Status is required.", nameof(status));

        var request = new ScanRequest
        {
            TableName = _tableName,
            Select = Select.COUNT,
            FilterExpression = "#status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "Status"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new AttributeValue { S = status }
            }
        };

        var count = 0;
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            count += response.Count ?? 0;

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return count;
    }

    public async Task<long> GetTotalChunkCountAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            ProjectionExpression = "ChunkCount"
        };

        long total = 0;
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            foreach (var item in response.Items)
            {
                if (item.TryGetValue("ChunkCount", out var chunkCount)
                    && long.TryParse(chunkCount.N, out var parsed))
                {
                    total += parsed;
                }
            }

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return total;
    }

    public async Task<IReadOnlyList<ExistingDocumentRecord>> GetAdminDocumentsAsync(
    CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            FilterExpression = "IsAdminDocument = :isAdminDocument",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":isAdminDocument"] = new AttributeValue
                {
                    BOOL = true
                }
            }
        };

        var documents = new List<ExistingDocumentRecord>();

        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            documents.AddRange(
                response.Items.Select(Map));

            request.ExclusiveStartKey =
                response.LastEvaluatedKey;

        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return documents
            .DistinctBy(x => x.DocumentId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToList();
    }

    public async Task<long> GetTotalPageCountAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName,
            ProjectionExpression = "PageCount"
        };

        long total = 0;
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            foreach (var item in response.Items)
            {
                if (item.TryGetValue("PageCount", out var pageCount)
                    && long.TryParse(pageCount.N, out var parsed))
                {
                    total += parsed;
                }
            }

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return total;
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
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("FileName is required.", nameof(fileName));

        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("StorageKey is required.", nameof(storageKey));

        var now = DateTime.UtcNow.ToString("O");

        var item = new Dictionary<string, AttributeValue>
        {
            ["DocumentId"] = new AttributeValue { S = documentId },
            ["OwnerUserId"] = new AttributeValue { S = ownerUserId },
            ["FileName"] = new AttributeValue { S = fileName },
            ["S3Key"] = new AttributeValue { S = storageKey },
            ["FileHash"] = new AttributeValue { S = fileHash },
            ["FileSizeBytes"] = new AttributeValue { N = fileSizeBytes.ToString(CultureInfo.InvariantCulture) },
            ["Status"] = new AttributeValue { S = "UPLOADED" },
            ["ChunkCount"] = new AttributeValue { N = "0" },
            ["PageCount"] = new AttributeValue { N = "0" },
            ["IsAdminDocument"] = new AttributeValue { BOOL = isAdminDocument },
            ["AllowedRoles"] = new AttributeValue
            {
                SS = allowedRoles
                    .Where(role => !string.IsNullOrWhiteSpace(role))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            },
            ["CreatedAtUtc"] = new AttributeValue { S = now },
            ["UpdatedAtUtc"] = new AttributeValue { S = now }
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(DocumentId)"
            }, token), cancellationToken);
    }

    public async Task<List<ExistingDocumentRecord>> GetDocumentsByUserAsync(
    string ownerUserId,
    CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var request = new ScanRequest
        {
            TableName = _tableName,
            FilterExpression = "OwnerUserId = :ownerUserId OR IsAdminDocument = :isAdminDocument",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":ownerUserId"] = new AttributeValue { S = ownerUserId },
                [":isAdminDocument"] = new AttributeValue { BOOL = true }
            }
        };

        var documents = new List<ExistingDocumentRecord>();
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);

            documents.AddRange(response.Items.Select(Map));

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return documents
            .DistinctBy(x => x.DocumentId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToList();
    }

    public async Task<int> GetDocumentCountAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var request = new ScanRequest
        {
            TableName = _tableName,
            Select = Select.COUNT,
            FilterExpression = "OwnerUserId = :ownerUserId OR IsAdminDocument = :isAdminDocument",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":ownerUserId"] = new AttributeValue { S = ownerUserId },
                [":isAdminDocument"] = new AttributeValue { BOOL = true }
            }
        };

        var count = 0;
        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _dynamoDb.ScanAsync(request, token),
                cancellationToken);
            count += response.Count ?? 0;
            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return count;
    }

    public async Task UpdateStatusAsync(
        string documentId,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Status is required.", nameof(status));

        await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["DocumentId"] = new AttributeValue { S = documentId }
                },
                UpdateExpression = "SET #status = :status, UpdatedAtUtc = :updatedAtUtc",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = "Status"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new AttributeValue { S = status },
                    [":updatedAtUtc"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
                }
            }, token), cancellationToken);
    }

    public async Task MarkIndexedAsync(
        string documentId,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePipeline.ExecuteAsync(
            async token => await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["DocumentId"] = new AttributeValue { S = documentId }
                },
                UpdateExpression =
                    "SET #status = :status, ChunkCount = :chunkCount, PageCount = :pageCount, UpdatedAtUtc = :updatedAtUtc",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = "Status"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new AttributeValue { S = "INDEXED" },
                    [":chunkCount"] = new AttributeValue
                    {
                        N = chunkCount.ToString(CultureInfo.InvariantCulture)
                    },
                    [":pageCount"] = new AttributeValue
                    {
                        N = Math.Max(pageCount, 0).ToString(CultureInfo.InvariantCulture)
                    },
                    [":updatedAtUtc"] = new AttributeValue
                    {
                        S = DateTime.UtcNow.ToString("O")
                    }
                }
            }, token), cancellationToken);
    }

    public async Task MarkFailedAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(
            documentId,
            "FAILED",
            cancellationToken);
    }

    private static ExistingDocumentRecord Map(Dictionary<string, AttributeValue> item)
    {
        return new ExistingDocumentRecord
        {
            DocumentId = GetString(item, "DocumentId"),
            OwnerUserId = GetString(item, "OwnerUserId"),
            FileName = GetString(item, "FileName"),
            StorageKey = GetString(item, "S3Key"),
            FileHash = GetString(item, "FileHash"),
            Status = GetString(item, "Status"),

            ChunkCount = GetInt(item, "ChunkCount"),
            PageCount = GetInt(item, "PageCount"),
            IsAdminDocument = GetBool(item, "IsAdminDocument"),
            AllowedRoles = GetStringList(item, "AllowedRoles"),

            CreatedAtUtc = GetDate(item, "CreatedAtUtc"),
            UpdatedAtUtc = GetDate(item, "UpdatedAtUtc")
        };
    }

    private static string GetString(
        Dictionary<string, AttributeValue> item,
        string key)
    {
        return item.TryGetValue(key, out var value)
            ? value.S ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(
        Dictionary<string, AttributeValue> item,
        string key)
    {
        if (!item.TryGetValue(key, out var value))
            return 0;

        return int.TryParse(value.N, out var parsed)
            ? parsed
            : 0;
    }

    private static DateTime GetDate(
        Dictionary<string, AttributeValue> item,
        string key)
    {
        if (!item.TryGetValue(key, out var value))
            return DateTime.UtcNow;

        return DateTime.TryParse(value.S, out var parsed)
            ? parsed
            : DateTime.UtcNow;
    }

    private static bool GetBool(
        Dictionary<string, AttributeValue> item,
        string key)
    {
        return item.TryGetValue(key, out var value) && value.BOOL == true;
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

    private static string? EncodeDocumentPageToken(
        Dictionary<string, AttributeValue>? lastEvaluatedKey)
    {
        if (lastEvaluatedKey is null ||
            lastEvaluatedKey.Count == 0 ||
            !lastEvaluatedKey.TryGetValue("DocumentId", out var documentId) ||
            string.IsNullOrWhiteSpace(documentId.S))
        {
            return null;
        }

        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(documentId.S));
    }

    private static Dictionary<string, AttributeValue>? DecodeDocumentPageToken(
        string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var documentId = Encoding.UTF8.GetString(
                Convert.FromBase64String(token));

            if (string.IsNullOrWhiteSpace(documentId))
                return null;

            return new Dictionary<string, AttributeValue>
            {
                ["DocumentId"] = new AttributeValue { S = documentId }
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
