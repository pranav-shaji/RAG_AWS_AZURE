using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.Interfaces;

public interface IConversationRepository
{
    Task UpsertSessionAsync(
        ConversationSession session,
        CancellationToken cancellationToken = default);

    Task<ConversationSession?> GetSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSession>> GetSessionsAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task AddMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string ownerUserId,
        string sessionId,
        int take,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        string ownerUserId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSession>> GetRecentSessionsAsync(
    int take = 50,
    CancellationToken cancellationToken = default);

    Task<int> GetTotalSessionCountAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetTotalMessageCountAsync(
        CancellationToken cancellationToken = default);
}
