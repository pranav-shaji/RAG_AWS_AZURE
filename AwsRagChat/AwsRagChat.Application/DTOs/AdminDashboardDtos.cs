namespace AwsRagChat.Application.DTOs;

public sealed class AdminDashboardStatsDto
{
    public int TotalUsers { get; set; }

    public int TotalDocuments { get; set; }

    public int IndexedDocuments { get; set; }

    public int FailedDocuments { get; set; }

    public int ProcessingDocuments { get; set; }

    public int TotalConversations { get; set; }

    public int TotalMessages { get; set; }

    public long TotalChunks { get; set; }

    public long TotalPages { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}

public sealed class AdminDocumentMonitoringDto
{
    public string DocumentId { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int ChunkCount { get; set; }

    public int PageCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<string> AllowedRoles { get; set; } = [];
}

public sealed class AdminDocumentMonitoringPageDto
{
    public List<AdminDocumentMonitoringDto> Items { get; set; } = [];

    public string? NextToken { get; set; }
}

public sealed class AdminConversationAnalyticsDto
{
    public string SessionId { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int MessageCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastMessageAtUtc { get; set; }
}

public sealed class AdminIngestionStatusDto
{
    public int Uploaded { get; set; }

    public int Indexed { get; set; }

    public int Failed { get; set; }

    public int Processing { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
