using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Options;
using System.Globalization;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class DynamoDbConversationRepository : IConversationRepository
{
    private readonly IAmazonDynamoDB _amazonDynamoDb;
    private readonly ConversationStorageOptions _conversationOptions;
    private readonly ResiliencePipeline _resiliencePipeline;

    public DynamoDbConversationRepository(
        IAmazonDynamoDB amazonDynamoDb,
        IOptions<ConversationStorageOptions> conversationOptions,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _amazonDynamoDb = amazonDynamoDb;
        _conversationOptions = conversationOptions.Value;
        _resiliencePipeline = pipelineProvider.GetPipeline("DynamoDbPipeline");
    }

    public async Task UpsertSessionAsync(
        ConversationSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue { S = BuildUserPk(session.OwnerUserId) },
            ["SK"] = new AttributeValue { S = BuildSessionSk(session.SessionId) },
            ["EntityType"] = new AttributeValue { S = "SESSION" },
            ["SessionId"] = new AttributeValue { S = session.SessionId },
            ["OwnerUserId"] = new AttributeValue { S = session.OwnerUserId },
            ["Title"] = new AttributeValue { S = session.Title ?? string.Empty },
            ["Summary"] = new AttributeValue { S = session.Summary ?? string.Empty },
            ["CreatedAtUtc"] = new AttributeValue { S = session.CreatedAtUtc.ToString("O") },
            ["UpdatedAtUtc"] = new AttributeValue { S = session.UpdatedAtUtc.ToString("O") },
            ["LastMessageAtUtc"] = new AttributeValue { S = session.LastMessageAtUtc.ToString("O") },
            ["MessageCount"] = new AttributeValue { N = session.MessageCount.ToString(CultureInfo.InvariantCulture) },
            ["IsArchived"] = new AttributeValue { BOOL = session.IsArchived }
        };

        var request = new PutItemRequest
        {
            TableName = _conversationOptions.TableName,
            Item = item
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.PutItemAsync(request, token),
            cancellationToken);
    }

    public async Task<ConversationSession?> GetSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var request = new GetItemRequest
        {
            TableName = _conversationOptions.TableName,
            ConsistentRead = true,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = BuildUserPk(ownerUserId) },
                ["SK"] = new AttributeValue { S = BuildSessionSk(sessionId) }
            }
        };

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.GetItemAsync(request, token),
            cancellationToken);

        if (response.Item is null || response.Item.Count == 0)
            return null;

        return MapSession(response.Item);
    }

    public async Task<IReadOnlyList<ConversationSession>> GetSessionsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var request = new QueryRequest
        {
            TableName = _conversationOptions.TableName,
            ConsistentRead = true,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new AttributeValue { S = BuildUserPk(ownerUserId) },
                [":skPrefix"] = new AttributeValue { S = "META#SESSION#" }
            }
        };

        var sessions = new List<ConversationSession>();
        QueryResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _amazonDynamoDb.QueryAsync(request, token),
                cancellationToken);

            sessions.AddRange(response.Items.Select(MapSession));

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return sessions
            .OrderByDescending(x => x.LastMessageAtUtc)
            .ToList();
    }

    public async Task AddMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue { S = BuildUserPk(message.OwnerUserId) },
            ["SK"] = new AttributeValue { S = BuildMessageSk(message.SessionId, message.CreatedAtUtc, message.MessageId) },
            ["EntityType"] = new AttributeValue { S = "MESSAGE" },
            ["SessionId"] = new AttributeValue { S = message.SessionId },
            ["MessageId"] = new AttributeValue { S = message.MessageId },
            ["OwnerUserId"] = new AttributeValue { S = message.OwnerUserId },
            ["Role"] = new AttributeValue { S = message.Role },
            ["Content"] = new AttributeValue { S = message.Content },
            ["CreatedAtUtc"] = new AttributeValue { S = message.CreatedAtUtc.ToString("O") },
            ["TokensApprox"] = new AttributeValue { N = message.TokensApprox.ToString(CultureInfo.InvariantCulture) },
            ["ResponseType"] = new AttributeValue { S = message.ResponseType ?? "text" },
            ["DataJson"] = new AttributeValue { S = message.DataJson ?? string.Empty },
            ["ChartDataJson"] = new AttributeValue { S = message.ChartDataJson ?? string.Empty },
            ["Citations"] = new AttributeValue
            {
                L = message.Citations.Select(MapCitationToAttributeValue).ToList()
            }
        };

        var request = new PutItemRequest
        {
            TableName = _conversationOptions.TableName,
            Item = item
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.PutItemAsync(request, token),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string ownerUserId,
        string sessionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be greater than zero.");

        var request = new QueryRequest
        {
            TableName = _conversationOptions.TableName,
            ConsistentRead = true,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new AttributeValue { S = BuildUserPk(ownerUserId) },
                [":skPrefix"] = new AttributeValue { S = $"SESSION#{sessionId}#MESSAGE#" }
            },
            ScanIndexForward = false,
            Limit = take
        };

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.QueryAsync(request, token),
            cancellationToken);

        return response.Items
            .Select(MapMessage)
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<ConversationSession>> GetRecentSessionsAsync(
    int take = 50,
    CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _conversationOptions.TableName,
            Limit = Math.Max(1, take)
        };

        var sessions = new List<ConversationSession>();

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.ScanAsync(request, token),
            cancellationToken);

        sessions.AddRange(
            response.Items
                .Where(x =>
                    x.TryGetValue("EntityType", out var entityType) &&
                    entityType.S == "SESSION")
                .Select(MapSession));

        return sessions
            .OrderByDescending(x => x.LastMessageAtUtc)
            .Take(take)
            .ToList();
    }

    public async Task<int> GetTotalSessionCountAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _conversationOptions.TableName
        };

        var total = 0;

        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _amazonDynamoDb.ScanAsync(request, token),
                cancellationToken);

            total += response.Items.Count(x =>
                x.TryGetValue("EntityType", out var entityType) &&
                entityType.S == "SESSION");

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return total;
    }

    public async Task<int> GetTotalMessageCountAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _conversationOptions.TableName
        };

        var total = 0;

        ScanResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _amazonDynamoDb.ScanAsync(request, token),
                cancellationToken);

            total += response.Items.Count(x =>
                x.TryGetValue("EntityType", out var entityType) &&
                entityType.S == "MESSAGE");

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return total;
    }

    public async Task DeleteSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var messageKeys = await GetMessageKeysAsync(ownerUserId, sessionId, cancellationToken);

        foreach (var batch in messageKeys.Chunk(25))
        {
            await DeleteBatchAsync(batch, cancellationToken);
        }

        var request = new DeleteItemRequest
        {
            TableName = _conversationOptions.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = BuildUserPk(ownerUserId) },
                ["SK"] = new AttributeValue { S = BuildSessionSk(sessionId) }
            }
        };

        await _resiliencePipeline.ExecuteAsync(
            async token => await _amazonDynamoDb.DeleteItemAsync(request, token),
            cancellationToken);
    }

    private async Task DeleteBatchAsync(
        IEnumerable<Dictionary<string, AttributeValue>> keys,
        CancellationToken cancellationToken)
    {
        var requestItems = keys
            .Select(key => new WriteRequest
            {
                DeleteRequest = new DeleteRequest
                {
                    Key = key
                }
            })
            .ToList();

        var request = new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>
            {
                [_conversationOptions.TableName] = requestItems
            }
        };

        do
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await _amazonDynamoDb.BatchWriteItemAsync(request, token),
                cancellationToken);

            request.RequestItems = response.UnprocessedItems;

            // DynamoDB can throttle batch deletes; retry unprocessed items so chat removal is complete.
            if (request.RequestItems.Count > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        while (request.RequestItems.Count > 0);
    }

    private async Task<List<Dictionary<string, AttributeValue>>> GetMessageKeysAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var request = new QueryRequest
        {
            TableName = _conversationOptions.TableName,
            ConsistentRead = true,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
            ProjectionExpression = "PK, SK",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new AttributeValue { S = BuildUserPk(ownerUserId) },
                [":skPrefix"] = new AttributeValue { S = $"SESSION#{sessionId}#MESSAGE#" }
            }
        };

        var keys = new List<Dictionary<string, AttributeValue>>();
        QueryResponse response;

        do
        {
            response = await _resiliencePipeline.ExecuteAsync(
                async token => await _amazonDynamoDb.QueryAsync(request, token),
                cancellationToken);

            keys.AddRange(response.Items.Select(item => new Dictionary<string, AttributeValue>
            {
                ["PK"] = item["PK"],
                ["SK"] = item["SK"]
            }));

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return keys;
    }

    private static AttributeValue MapCitationToAttributeValue(Citation citation)
    {
        return new AttributeValue
        {
            M = new Dictionary<string, AttributeValue>
            {
                ["DocumentId"] = new AttributeValue { S = citation.DocumentId ?? string.Empty },
                ["ChunkId"] = new AttributeValue { S = citation.ChunkId ?? string.Empty },
                ["FileName"] = new AttributeValue { S = citation.FileName ?? string.Empty },
                ["PageNumber"] = new AttributeValue { N = citation.PageNumber.ToString(CultureInfo.InvariantCulture) },
                ["Snippet"] = new AttributeValue { S = citation.Snippet ?? string.Empty }
            }
        };
    }

    private static List<Citation> MapCitations(AttributeValue? attributeValue)
    {
        if (attributeValue?.L is null || attributeValue.L.Count == 0)
            return [];

        var citations = new List<Citation>();

        foreach (var item in attributeValue.L)
        {
            if (item.M is null)
                continue;

            citations.Add(new Citation
            {
                DocumentId = item.M.TryGetValue("DocumentId", out var documentId) ? documentId.S : string.Empty,
                ChunkId = item.M.TryGetValue("ChunkId", out var chunkId) ? chunkId.S : string.Empty,
                FileName = item.M.TryGetValue("FileName", out var fileName) ? fileName.S : string.Empty,
                PageNumber = item.M.TryGetValue("PageNumber", out var pageNumber) && int.TryParse(pageNumber.N, out var parsedPageNumber)
                    ? parsedPageNumber
                    : 0,
                Snippet = item.M.TryGetValue("Snippet", out var snippet) ? snippet.S : string.Empty
            });
        }

        return citations;
    }

    private static ConversationSession MapSession(Dictionary<string, AttributeValue> item)
    {
        return new ConversationSession
        {
            SessionId = item.TryGetValue("SessionId", out var sessionId) ? sessionId.S : string.Empty,
            OwnerUserId = item.TryGetValue("OwnerUserId", out var ownerUserId) ? ownerUserId.S : string.Empty,
            Title = item.TryGetValue("Title", out var title) ? title.S : "New chat",
            Summary = item.TryGetValue("Summary", out var summary) ? summary.S : string.Empty,
            CreatedAtUtc = item.TryGetValue("CreatedAtUtc", out var createdAtUtc)
                           && DateTime.TryParse(createdAtUtc.S, null, DateTimeStyles.RoundtripKind, out var createdAt)
                ? createdAt
                : DateTime.UtcNow,
            UpdatedAtUtc = item.TryGetValue("UpdatedAtUtc", out var updatedAtUtc)
                           && DateTime.TryParse(updatedAtUtc.S, null, DateTimeStyles.RoundtripKind, out var updatedAt)
                ? updatedAt
                : DateTime.UtcNow,
            LastMessageAtUtc = item.TryGetValue("LastMessageAtUtc", out var lastMessageAtUtc)
                               && DateTime.TryParse(lastMessageAtUtc.S, null, DateTimeStyles.RoundtripKind, out var lastMessageAt)
                ? lastMessageAt
                : DateTime.UtcNow,
            MessageCount = item.TryGetValue("MessageCount", out var messageCount)
                           && int.TryParse(messageCount.N, out var parsedMessageCount)
                ? parsedMessageCount
                : 0,
            IsArchived = item.TryGetValue("IsArchived", out var isArchived) && isArchived.BOOL == true
        };
    }

    private static ConversationMessage MapMessage(Dictionary<string, AttributeValue> item)
    {
        return new ConversationMessage
        {
            SessionId = item.TryGetValue("SessionId", out var sessionId) ? sessionId.S : string.Empty,
            MessageId = item.TryGetValue("MessageId", out var messageId) ? messageId.S : string.Empty,
            OwnerUserId = item.TryGetValue("OwnerUserId", out var ownerUserId) ? ownerUserId.S : string.Empty,
            Role = item.TryGetValue("Role", out var role) ? role.S : string.Empty,
            Content = item.TryGetValue("Content", out var content) ? content.S : string.Empty,
            CreatedAtUtc = item.TryGetValue("CreatedAtUtc", out var createdAtUtc)
                           && DateTime.TryParse(createdAtUtc.S, null, DateTimeStyles.RoundtripKind, out var createdAt)
                ? createdAt
                : DateTime.UtcNow,
            TokensApprox = item.TryGetValue("TokensApprox", out var tokensApprox)
                           && int.TryParse(tokensApprox.N, out var parsedTokensApprox)
                ? parsedTokensApprox
                : 0,
            ResponseType = item.TryGetValue("ResponseType", out var responseType) ? responseType.S : "text",
            DataJson = item.TryGetValue("DataJson", out var dataJson) ? dataJson.S : string.Empty,
            ChartDataJson = item.TryGetValue("ChartDataJson", out var chartDataJson) ? chartDataJson.S : string.Empty,
            Citations = item.TryGetValue("Citations", out var citations)
                ? MapCitations(citations)
                : []
        };
    }

    private static string BuildUserPk(string ownerUserId) => $"USER#{ownerUserId}";
    private static string BuildSessionSk(string sessionId) => $"META#SESSION#{sessionId}";
    private static string BuildMessageSk(string sessionId, DateTime createdAtUtc, string messageId)
        => $"SESSION#{sessionId}#MESSAGE#{createdAtUtc.Ticks:D19}#{messageId}";
}
