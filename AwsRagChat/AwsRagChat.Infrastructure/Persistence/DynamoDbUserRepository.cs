using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class DynamoDbUserRepository : IUserRepository
{
    private const string DefaultTableName = "aws-rag-chat-users";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbUserRepository(
        IAmazonDynamoDB dynamoDb,
        IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _tableName = configuration["Users:TableName"] ?? DefaultTableName;
    }

    public async Task<EnterpriseUserDto?> GetByUserIdAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));

        var response = await _dynamoDb.GetItemAsync(
            new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["UserId"] = new AttributeValue { S = userId }
                }
            },
            cancellationToken);

        return response.Item is { Count: > 0 }
            ? Map(response.Item)
            : null;
    }

    public async Task<IReadOnlyList<EnterpriseUserDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new ScanRequest
        {
            TableName = _tableName
        };

        var users = new List<EnterpriseUserDto>();
        ScanResponse response;

        do
        {
            response = await _dynamoDb.ScanAsync(
                request,
                cancellationToken);

            users.AddRange(response.Items.Select(Map));

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }
        while (response.LastEvaluatedKey is { Count: > 0 });

        return users
            .OrderBy(user => user.ApprovalStatus)
            .ThenByDescending(user => user.CreatedAtUtc)
            .ToList();
    }

    public async Task UpsertAsync(
        EnterpriseUserDto user,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.UserId))
            throw new ArgumentException("UserId is required.", nameof(user));

        var createdAt = user.CreatedAtUtc == default
            ? DateTime.UtcNow
            : user.CreatedAtUtc;

        var updatedAt = user.UpdatedAtUtc == default
            ? DateTime.UtcNow
            : user.UpdatedAtUtc;

        var item = new Dictionary<string, AttributeValue>
        {
            ["UserId"] = new AttributeValue { S = user.UserId },
            ["Email"] = new AttributeValue { S = user.Email ?? string.Empty },
            ["ApprovalStatus"] = new AttributeValue { S = user.ApprovalStatus ?? string.Empty },
            ["ApprovedRole"] = new AttributeValue { S = user.ApprovedRole ?? string.Empty },
            ["CreatedAtUtc"] = new AttributeValue { S = createdAt.ToString("O") },
            ["UpdatedAtUtc"] = new AttributeValue { S = updatedAt.ToString("O") },
            ["ApprovedBy"] = new AttributeValue { S = user.ApprovedBy ?? string.Empty }
        };

        if (user.ApprovedAtUtc.HasValue)
            item["ApprovedAtUtc"] = new AttributeValue { S = user.ApprovedAtUtc.Value.ToString("O") };

        await _dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            },
            cancellationToken);
    }

    private static EnterpriseUserDto Map(Dictionary<string, AttributeValue> item)
    {
        return new EnterpriseUserDto
        {
            UserId = GetString(item, "UserId"),
            Email = GetString(item, "Email"),
            ApprovalStatus = GetString(item, "ApprovalStatus"),
            ApprovedRole = GetString(item, "ApprovedRole"),
            CreatedAtUtc = GetDate(item, "CreatedAtUtc"),
            UpdatedAtUtc = GetDate(item, "UpdatedAtUtc"),
            ApprovedBy = GetString(item, "ApprovedBy"),
            ApprovedAtUtc = GetNullableDate(item, "ApprovedAtUtc")
        };
    }

    private static string GetString(
        Dictionary<string, AttributeValue> item,
        string key) =>
        item.TryGetValue(key, out var value)
            ? value.S ?? string.Empty
            : string.Empty;

    private static DateTime GetDate(
        Dictionary<string, AttributeValue> item,
        string key) =>
        item.TryGetValue(key, out var value) &&
        DateTime.TryParse(value.S, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static DateTime? GetNullableDate(
        Dictionary<string, AttributeValue> item,
        string key) =>
        item.TryGetValue(key, out var value) &&
        DateTime.TryParse(value.S, out var parsed)
            ? parsed
            : null;
}
