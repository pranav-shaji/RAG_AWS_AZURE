using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class CosmosDbUserRepository : IUserRepository
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosDbOptions _options;
    private Container _container = null!;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public CosmosDbUserRepository(
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
                new ContainerProperties(_options.UsersContainer, "/id"),
                cancellationToken: cancellationToken);

            _container = containerResponse.Container;
            _isInitialized = true;
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task<EnterpriseUserDto?> GetByUserIdAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));

        await EnsureInitializedAsync(cancellationToken);

        try
        {
            var response = await _container.ReadItemAsync<CosmosUserModel>(
                userId,
                new PartitionKey(userId),
                cancellationToken: cancellationToken);

            return Map(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EnterpriseUserDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query = new QueryDefinition("SELECT * FROM c");
        var iterator = _container.GetItemQueryIterator<CosmosUserModel>(query);
        var results = new List<EnterpriseUserDto>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(response.Select(Map));
        }

        return results
            .OrderBy(user => user.ApprovalStatus)
            .ThenByDescending(user => user.CreatedAtUtc)
            .ToList();
    }

    public async Task UpsertAsync(
        EnterpriseUserDto user,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.UserId))
            throw new ArgumentException("UserId is required.", nameof(user.UserId));

        await EnsureInitializedAsync(cancellationToken);

        var createdAt = user.CreatedAtUtc == default ? DateTime.UtcNow : user.CreatedAtUtc;
        var updatedAt = user.UpdatedAtUtc == default ? DateTime.UtcNow : user.UpdatedAtUtc;

        var model = new CosmosUserModel
        {
            Id = user.UserId,
            Email = user.Email ?? string.Empty,
            ApprovalStatus = user.ApprovalStatus ?? string.Empty,
            ApprovedRole = user.ApprovedRole ?? string.Empty,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            ApprovedBy = user.ApprovedBy ?? string.Empty,
            ApprovedAtUtc = user.ApprovedAtUtc
        };

        await _container.UpsertItemAsync(model, new PartitionKey(user.UserId), cancellationToken: cancellationToken);
    }

    private static EnterpriseUserDto Map(CosmosUserModel model)
    {
        return new EnterpriseUserDto
        {
            UserId = model.Id,
            Email = model.Email,
            ApprovalStatus = model.ApprovalStatus,
            ApprovedRole = model.ApprovedRole,
            CreatedAtUtc = model.CreatedAtUtc,
            UpdatedAtUtc = model.UpdatedAtUtc,
            ApprovedBy = model.ApprovedBy,
            ApprovedAtUtc = model.ApprovedAtUtc
        };
    }

    private sealed class CosmosUserModel
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("email")]
        public string Email { get; set; } = string.Empty;

        [JsonProperty("approvalStatus")]
        public string ApprovalStatus { get; set; } = string.Empty;

        [JsonProperty("approvedRole")]
        public string ApprovedRole { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; }

        [JsonProperty("approvedBy")]
        public string ApprovedBy { get; set; } = string.Empty;

        [JsonProperty("approvedAtUtc")]
        public DateTime? ApprovedAtUtc { get; set; }
    }
}
