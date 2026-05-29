using AwsRagChat.Application.DTOs;

namespace AwsRagChat.Application.Interfaces;

public interface IAdminAnalyticsService
{
    Task<AdminDashboardStatsDto> GetDashboardStatsAsync(
        CancellationToken cancellationToken = default);

    Task<AdminDocumentMonitoringPageDto> GetDocumentsPageAsync(
        int pageSize = 20,
        string? nextToken = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminConversationAnalyticsDto>> GetRecentConversationsAsync(
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<AdminIngestionStatusDto> GetIngestionStatusAsync(
        CancellationToken cancellationToken = default);
}
