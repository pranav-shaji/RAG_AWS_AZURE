using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Persistence;

public sealed class CosmosDbConversationRepository : IConversationRepository
{
    private readonly CosmosClient _cosmosClient;
    private readonly CosmosDbOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;
    private Container _container = null!;
    private static readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private static bool _isInitialized;

    public CosmosDbConversationRepository(
        CosmosClient cosmosClient,
        IOptions<CosmosDbOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _cosmosClient = cosmosClient;
        _options = options.Value;
        _resiliencePipeline = pipelineProvider.GetPipeline("CosmosDbPipeline");
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

            var dbResponse = await _resiliencePipeline.ExecuteAsync(
                async token => await _cosmosClient.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, cancellationToken: token),
                cancellationToken);
            var containerResponse = await _resiliencePipeline.ExecuteAsync(
                async token => await dbResponse.Database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(_options.ConversationsContainer, "/ownerUserId"),
                    cancellationToken: token),
                cancellationToken);

            _container = containerResponse.Container;
            _isInitialized = true;
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    public async Task UpsertSessionAsync(
        ConversationSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.UpsertSession",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("session.id", session.SessionId);
        activity?.SetTag("user.id", session.OwnerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var model = new CosmosSessionModel
        {
            Id = $"session_{session.SessionId}",
            EntityType = "SESSION",
            SessionId = session.SessionId,
            OwnerUserId = session.OwnerUserId,
            Title = session.Title ?? string.Empty,
            Summary = session.Summary ?? string.Empty,
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
            LastMessageAtUtc = session.LastMessageAtUtc,
            MessageCount = session.MessageCount,
            IsArchived = session.IsArchived
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _resiliencePipeline.ExecuteAsync(
                async token => await _container.UpsertItemAsync(model, new PartitionKey(session.OwnerUserId), cancellationToken: token),
                cancellationToken);

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "UpsertSession"));
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
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

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetSession",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("session.id", sessionId);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await _container.ReadItemAsync<CosmosSessionModel>(
                    $"session_{sessionId}",
                    new PartitionKey(ownerUserId),
                    cancellationToken: token),
                cancellationToken);

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "ReadSession"));

            return MapSession(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "ReadSession"));
            return null;
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationSession>> GetSessionsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetSessions",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.ownerUserId = @ownerUserId AND c.entityType = 'SESSION'")
                .WithParameter("@ownerUserId", ownerUserId);

            var iterator = _container.GetItemQueryIterator<CosmosSessionModel>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(ownerUserId) });

            var results = new List<ConversationSession>();
            while (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                results.AddRange(response.Select(MapSession));
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "QuerySessions"));

            return results
                .OrderByDescending(x => x.LastMessageAtUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task AddMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.AddMessage",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("session.id", message.SessionId);
        activity?.SetTag("message.id", message.MessageId);
        activity?.SetTag("user.id", message.OwnerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var model = new CosmosMessageModel
        {
            Id = $"msg_{message.MessageId}",
            EntityType = "MESSAGE",
            SessionId = message.SessionId,
            MessageId = message.MessageId,
            OwnerUserId = message.OwnerUserId,
            Role = message.Role,
            Content = message.Content,
            CreatedAtUtc = message.CreatedAtUtc,
            TokensApprox = message.TokensApprox,
            ResponseType = message.ResponseType ?? "text",
            DataJson = message.DataJson ?? string.Empty,
            ChartDataJson = message.ChartDataJson ?? string.Empty,
            Citations = message.Citations.Select(MapCitation).ToList()
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _resiliencePipeline.ExecuteAsync(
                async token => await _container.UpsertItemAsync(model, new PartitionKey(message.OwnerUserId), cancellationToken: token),
                cancellationToken);

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "AddMessage"));
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
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

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetMessages",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("session.id", sessionId);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.ownerUserId = @ownerUserId AND c.sessionId = @sessionId AND c.entityType = 'MESSAGE' ORDER BY c.createdAtUtc DESC OFFSET 0 LIMIT @take")
                .WithParameter("@ownerUserId", ownerUserId)
                .WithParameter("@sessionId", sessionId)
                .WithParameter("@take", take);

            var iterator = _container.GetItemQueryIterator<CosmosMessageModel>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(ownerUserId) });

            var results = new List<ConversationMessage>();
            while (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                results.AddRange(response.Select(MapMessage));
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "QueryMessages"));

            return results
                .OrderBy(x => x.CreatedAtUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
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

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.DeleteSession",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);
        activity?.SetTag("session.id", sessionId);
        activity?.SetTag("user.id", ownerUserId);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var query = new QueryDefinition("SELECT c.id FROM c WHERE c.ownerUserId = @ownerUserId AND (c.id = @sessionKey OR (c.sessionId = @sessionId AND c.entityType = 'MESSAGE'))")
                .WithParameter("@ownerUserId", ownerUserId)
                .WithParameter("@sessionKey", $"session_{sessionId}")
                .WithParameter("@sessionId", sessionId);

            var iterator = _container.GetItemQueryIterator<IdProjection>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(ownerUserId) });

            var idsToDelete = new List<string>();
            while (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                idsToDelete.AddRange(response.Select(x => x.Id));
            }

            foreach (var id in idsToDelete)
            {
                await _resiliencePipeline.ExecuteAsync(
                    async token => await _container.DeleteItemAsync<object>(id, new PartitionKey(ownerUserId), cancellationToken: token),
                    cancellationToken);
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "DeleteSession"));
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationSession>> GetRecentSessionsAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetRecentSessions",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);

        await EnsureInitializedAsync(cancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.entityType = 'SESSION' ORDER BY c.lastMessageAtUtc DESC OFFSET 0 LIMIT @take")
                .WithParameter("@take", take);

            var iterator = _container.GetItemQueryIterator<CosmosSessionModel>(query);
            var results = new List<ConversationSession>();

            if (iterator.HasMoreResults)
            {
                var response = await _resiliencePipeline.ExecuteAsync(
                    async token => await iterator.ReadNextAsync(token),
                    cancellationToken);
                results.AddRange(response.Select(MapSession));
            }

            stopwatch.Stop();
            AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.DbLatencyHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("database", "CosmosDb"),
                new KeyValuePair<string, object?>("container", _options.ConversationsContainer),
                new KeyValuePair<string, object?>("operation", "QueryRecentSessions"));

            return results;
        }
        catch (Exception ex)
        {
            activity?.SetTag("error", ex.Message);
            throw;
        }
    }

    public async Task<int> GetTotalSessionCountAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetTotalSessionCount",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);

        await EnsureInitializedAsync(cancellationToken);
        return await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.entityType = 'SESSION'"), cancellationToken);
    }

    public async Task<int> GetTotalMessageCountAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "CosmosDb.GetTotalMessageCount",
            System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("db.name", _options.DatabaseName);
        activity?.SetTag("db.container", _options.ConversationsContainer);

        await EnsureInitializedAsync(cancellationToken);
        return await GetScalarAsync<int>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.entityType = 'MESSAGE'"), cancellationToken);
    }

    private async Task<T> GetScalarAsync<T>(QueryDefinition queryDefinition, CancellationToken cancellationToken)
    {
        var iterator = _container.GetItemQueryIterator<T?>(queryDefinition);
        if (iterator.HasMoreResults)
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async token => await iterator.ReadNextAsync(token),
                cancellationToken);
            var val = response.FirstOrDefault();
            return val ?? default!;
        }
        return default!;
    }

    private static ConversationSession MapSession(CosmosSessionModel model)
    {
        return new ConversationSession
        {
            SessionId = model.SessionId,
            OwnerUserId = model.OwnerUserId,
            Title = model.Title,
            Summary = model.Summary,
            CreatedAtUtc = model.CreatedAtUtc,
            UpdatedAtUtc = model.UpdatedAtUtc,
            LastMessageAtUtc = model.LastMessageAtUtc,
            MessageCount = model.MessageCount,
            IsArchived = model.IsArchived
        };
    }

    private static ConversationMessage MapMessage(CosmosMessageModel model)
    {
        return new ConversationMessage
        {
            SessionId = model.SessionId,
            MessageId = model.MessageId,
            OwnerUserId = model.OwnerUserId,
            Role = model.Role,
            Content = model.Content,
            CreatedAtUtc = model.CreatedAtUtc,
            TokensApprox = model.TokensApprox,
            ResponseType = model.ResponseType,
            DataJson = model.DataJson,
            ChartDataJson = model.ChartDataJson,
            Citations = model.Citations.Select(MapCitationModel).ToList()
        };
    }

    private static CosmosCitationModel MapCitation(Citation citation)
    {
        return new CosmosCitationModel
        {
            DocumentId = citation.DocumentId ?? string.Empty,
            ChunkId = citation.ChunkId ?? string.Empty,
            FileName = citation.FileName ?? string.Empty,
            PageNumber = citation.PageNumber,
            Snippet = citation.Snippet ?? string.Empty
        };
    }

    private static Citation MapCitationModel(CosmosCitationModel model)
    {
        return new Citation
        {
            DocumentId = model.DocumentId,
            ChunkId = model.ChunkId,
            FileName = model.FileName,
            PageNumber = model.PageNumber,
            Snippet = model.Snippet
        };
    }

    private sealed class IdProjection
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class CosmosSessionModel
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("entityType")]
        public string EntityType { get; set; } = "SESSION";

        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("ownerUserId")]
        public string OwnerUserId { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; }

        [JsonProperty("lastMessageAtUtc")]
        public DateTime LastMessageAtUtc { get; set; }

        [JsonProperty("messageCount")]
        public int MessageCount { get; set; }

        [JsonProperty("isArchived")]
        public bool IsArchived { get; set; }
    }

    private sealed class CosmosMessageModel
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("entityType")]
        public string EntityType { get; set; } = "MESSAGE";

        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("messageId")]
        public string MessageId { get; set; } = string.Empty;

        [JsonProperty("ownerUserId")]
        public string OwnerUserId { get; set; } = string.Empty;

        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("tokensApprox")]
        public int TokensApprox { get; set; }

        [JsonProperty("responseType")]
        public string ResponseType { get; set; } = "text";

        [JsonProperty("dataJson")]
        public string DataJson { get; set; } = string.Empty;

        [JsonProperty("chartDataJson")]
        public string ChartDataJson { get; set; } = string.Empty;

        [JsonProperty("citations")]
        public List<CosmosCitationModel> Citations { get; set; } = [];
    }

    private sealed class CosmosCitationModel
    {
        [JsonProperty("documentId")]
        public string DocumentId { get; set; } = string.Empty;

        [JsonProperty("chunkId")]
        public string ChunkId { get; set; } = string.Empty;

        [JsonProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonProperty("pageNumber")]
        public int PageNumber { get; set; }

        [JsonProperty("snippet")]
        public string Snippet { get; set; } = string.Empty;
    }
}
