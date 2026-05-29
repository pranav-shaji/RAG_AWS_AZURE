using AwsRagChat.Domain.Entities;

namespace AwsRagChat.Application.Interfaces;

public interface IChatCompletionService
{
    Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> relevantChunks,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat,
        CancellationToken cancellationToken = default);

    Task<string> GenerateGeneralAnswerAsync(
        string question,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat,
        CancellationToken cancellationToken = default);

    Task<string> GenerateKnowledgeOverviewAsync(
        IReadOnlyList<string> knowledgeSignals,
        CancellationToken cancellationToken = default);
}
