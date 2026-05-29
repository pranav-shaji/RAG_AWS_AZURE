using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;

namespace AwsRagChat.Application.Services;

public sealed class AdminAnalyticsService : IAdminAnalyticsService
{
    private readonly IDocumentRepository _documentRepository;

    private readonly IConversationRepository _conversationRepository;

    public AdminAnalyticsService(
        IDocumentRepository documentRepository,
        IConversationRepository conversationRepository)
    {
        _documentRepository = documentRepository;
        _conversationRepository = conversationRepository;
    }

    public async Task<AdminDashboardStatsDto> GetDashboardStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var documentStats =
            await _documentRepository.GetDocumentStatsSnapshotAsync(
                cancellationToken);

        var totalConversations =
            await _conversationRepository.GetTotalSessionCountAsync(
                cancellationToken);

        var totalMessages =
            await _conversationRepository.GetTotalMessageCountAsync(
                cancellationToken);

        return new AdminDashboardStatsDto
        {
            TotalUsers = 0,

            TotalDocuments = documentStats.TotalDocuments,

            IndexedDocuments = documentStats.IndexedDocuments,

            FailedDocuments = documentStats.FailedDocuments,

            ProcessingDocuments = documentStats.UploadedDocuments,

            TotalConversations = totalConversations,

            TotalMessages = totalMessages,

            TotalChunks = documentStats.TotalChunks,

            TotalPages = documentStats.TotalPages,

            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<AdminDocumentMonitoringPageDto> GetDocumentsPageAsync(
        int pageSize = 20,
        string? nextToken = null,
        CancellationToken cancellationToken = default)
    {
        var page =
            await _documentRepository.GetDocumentMetadataPageAsync(
                pageSize,
                nextToken,
                cancellationToken);

        return new AdminDocumentMonitoringPageDto
        {
            Items = page.Items.Select(x => new AdminDocumentMonitoringDto
            {
                DocumentId = x.DocumentId,
                OwnerUserId = x.OwnerUserId,
                FileName = x.FileName,
                Status = x.Status,
                ChunkCount = x.ChunkCount,
                PageCount = x.PageCount,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                AllowedRoles = x.AllowedRoles
            })
            .ToList(),
            NextToken = page.NextToken
        };
    }

    public async Task<IReadOnlyList<AdminConversationAnalyticsDto>> GetRecentConversationsAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var conversations =
            await _conversationRepository.GetRecentSessionsAsync(
                take,
                cancellationToken);

        return conversations
            .Select(x => new AdminConversationAnalyticsDto
            {
                SessionId = x.SessionId,
                OwnerUserId = x.OwnerUserId,
                Title = x.Title,
                MessageCount = x.MessageCount,
                CreatedAtUtc = x.CreatedAtUtc,
                LastMessageAtUtc = x.LastMessageAtUtc
            })
            .ToList();
    }

    public async Task<AdminIngestionStatusDto> GetIngestionStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var documentStats =
            await _documentRepository.GetDocumentStatsSnapshotAsync(
                cancellationToken);

        return new AdminIngestionStatusDto
        {
            Uploaded = documentStats.UploadedDocuments,

            Indexed = documentStats.IndexedDocuments,

            Failed = documentStats.FailedDocuments,

            Processing = documentStats.UploadedDocuments,

            GeneratedAtUtc = DateTime.UtcNow
        };
    }

}
