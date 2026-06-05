using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Application.Interfaces;
using System.Globalization;

namespace AwsRagChat.Ingestion.Services;

public sealed class DocumentStatusService : IDocumentStatusService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DocumentStatusService(
        IAmazonDynamoDB dynamoDb,
        string tableName = "rag-documents")
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
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

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["DocumentId"] = new AttributeValue { S = documentId }
            },
            ProjectionExpression = "IsAdminDocument"
        }, cancellationToken);

        return response.Item.TryGetValue("IsAdminDocument", out var isAdminDocument) && isAdminDocument.BOOL == true;
    }

    public async Task<List<string>> GetAllowedRolesAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["DocumentId"] = new AttributeValue { S = documentId }
            },
            ProjectionExpression = "AllowedRoles"
        }, cancellationToken);

        if (!response.Item.TryGetValue("AllowedRoles", out var allowedRoles))
            return [];

        if (allowedRoles.SS is { Count: > 0 })
            return allowedRoles.SS
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (allowedRoles.L is { Count: > 0 })
            return allowedRoles.L
                .Select(role => role.S ?? string.Empty)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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

        var now = DateTime.UtcNow.ToString("O");

        var expressionAttributeNames = new Dictionary<string, string>
        {
            ["#status"] = "Status",
            ["#ownerUserId"] = "OwnerUserId",
            ["#fileName"] = "FileName",
            ["#s3Key"] = "S3Key",
            ["#updatedAtUtc"] = "UpdatedAtUtc"
        };

        var expressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":status"] = new AttributeValue { S = status },
            [":ownerUserId"] = new AttributeValue { S = ownerUserId },
            [":fileName"] = new AttributeValue { S = fileName },
            [":s3Key"] = new AttributeValue { S = storageKey },
            [":updatedAtUtc"] = new AttributeValue { S = now }
        };

        var updateExpression =
            "SET #status = :status, #ownerUserId = :ownerUserId, #fileName = :fileName, #s3Key = :s3Key, #updatedAtUtc = :updatedAtUtc";

        if (!string.IsNullOrWhiteSpace(textractJobId))
        {
            expressionAttributeNames["#textractJobId"] = "TextractJobId";
            expressionAttributeValues[":textractJobId"] = new AttributeValue { S = textractJobId };

            updateExpression += ", #textractJobId = :textractJobId";
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            expressionAttributeNames["#message"] = "Message";
            expressionAttributeValues[":message"] = new AttributeValue { S = message };

            updateExpression += ", #message = :message";
        }

        if (chunkCount.HasValue)
        {
            expressionAttributeNames["#chunkCount"] = "ChunkCount";
            expressionAttributeValues[":chunkCount"] = new AttributeValue
            {
                N = chunkCount.Value.ToString(CultureInfo.InvariantCulture)
            };

            updateExpression += ", #chunkCount = :chunkCount";
        }

        if (pageCount.HasValue)
        {
            expressionAttributeNames["#pageCount"] = "PageCount";
            expressionAttributeValues[":pageCount"] = new AttributeValue
            {
                N = Math.Max(pageCount.Value, 0).ToString(CultureInfo.InvariantCulture)
            };

            updateExpression += ", #pageCount = :pageCount";
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["DocumentId"] = new AttributeValue
                {
                    S = documentId
                }
            },
            UpdateExpression = updateExpression,
            ExpressionAttributeNames = expressionAttributeNames,
            ExpressionAttributeValues = expressionAttributeValues
        }, cancellationToken);
    }
}
