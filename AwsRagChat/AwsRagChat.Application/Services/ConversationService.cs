using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using System.Text.Json;

namespace AwsRagChat.Application.Services;

public sealed class ConversationService
{
    private readonly IConversationRepository _conversationRepository;

    public ConversationService(IConversationRepository conversationRepository)
    {
        _conversationRepository = conversationRepository;
    }

    public async Task<ConversationSessionDto> CreateSessionAsync(
        string ownerUserId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var now = DateTime.UtcNow;

        var session = new ConversationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            OwnerUserId = ownerUserId,
            Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim(),
            Summary = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastMessageAtUtc = now,
            MessageCount = 0,
            IsArchived = false
        };

        await _conversationRepository.UpsertSessionAsync(session, cancellationToken);

        return MapSession(session);
    }

    public async Task<IReadOnlyList<ConversationSessionDto>> GetSessionsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        var sessions = await _conversationRepository.GetSessionsAsync(ownerUserId, cancellationToken);

        return sessions
            .Where(x => !x.IsArchived)
            .OrderByDescending(x => x.LastMessageAtUtc)
            .Select(MapSession)
            .ToList();
    }

    public async Task<ConversationSessionDto?> GetSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var session = await _conversationRepository.GetSessionAsync(ownerUserId, sessionId, cancellationToken);

        return session is null || session.IsArchived
            ? null
            : MapSession(session);
    }

    public async Task<IReadOnlyList<ConversationMessageDto>> GetMessagesAsync(
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

        var session = await _conversationRepository.GetSessionAsync(ownerUserId, sessionId, cancellationToken);

        if (session is null || session.IsArchived)
            return [];

        var messages = await _conversationRepository.GetMessagesAsync(
            ownerUserId,
            sessionId,
            take,
            cancellationToken);

        return messages
            .OrderBy(x => x.CreatedAtUtc)
            .Select(MapMessage)
            .ToList();
    }

    public async Task<bool> DeleteSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var session = await _conversationRepository.GetSessionAsync(
            ownerUserId,
            sessionId,
            cancellationToken);

        if (session is null || session.IsArchived)
            return false;

        await _conversationRepository.DeleteSessionAsync(
            ownerUserId,
            sessionId,
            cancellationToken);

        return true;
    }

    private static ConversationSessionDto MapSession(ConversationSession session)
    {
        return new ConversationSessionDto
        {
            SessionId = session.SessionId,
            Title = session.Title,
            Summary = session.Summary,
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
            LastMessageAtUtc = session.LastMessageAtUtc,
            MessageCount = session.MessageCount,
            IsArchived = session.IsArchived
        };
    }

    private static ConversationMessageDto MapMessage(ConversationMessage message)
    {
        return new ConversationMessageDto
        {
            MessageId = message.MessageId,
            SessionId = message.SessionId,
            Role = message.Role,
            Content = message.Content,
            CreatedAtUtc = message.CreatedAtUtc,
            TokensApprox = message.TokensApprox,
            Citations = message.Citations,
            ResponseType = string.IsNullOrWhiteSpace(message.ResponseType)
                ? "text"
                : message.ResponseType,
            Data = DeserializeData(message.DataJson),
            ChartData = DeserializeChartData(message.ChartDataJson)
        };
    }

    private static object? DeserializeData(string dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(dataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChartData? DeserializeChartData(string chartDataJson)
    {
        if (string.IsNullOrWhiteSpace(chartDataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ChartData>(
                chartDataJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
