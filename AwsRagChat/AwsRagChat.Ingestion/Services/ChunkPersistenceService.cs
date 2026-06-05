using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Ingestion.Options;
using Microsoft.Extensions.Options;

namespace AwsRagChat.Ingestion.Services;

public sealed class ChunkPersistenceService
{
    private readonly IAmazonDynamoDB _amazonDynamoDb;
    private readonly DynamoDbOptions _dynamoDbOptions;

    public ChunkPersistenceService(
        IAmazonDynamoDB amazonDynamoDb,
        IOptions<DynamoDbOptions> dynamoDbOptions)
    {
        _amazonDynamoDb = amazonDynamoDb;
        _dynamoDbOptions = dynamoDbOptions.Value;
    }

    public async Task SaveChunksAsync(
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["OwnerUserId"] = new AttributeValue { S = chunk.OwnerUserId },
                ["DocumentId"] = new AttributeValue { S = chunk.DocumentId },
                ["ChunkId"] = new AttributeValue { S = chunk.ChunkId },
                ["FileName"] = new AttributeValue { S = chunk.FileName },
                ["S3Key"] = new AttributeValue { S = chunk.StorageKey },
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

            await _amazonDynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _dynamoDbOptions.TableName,
                Item = item
            }, cancellationToken);
        }
    }
}
