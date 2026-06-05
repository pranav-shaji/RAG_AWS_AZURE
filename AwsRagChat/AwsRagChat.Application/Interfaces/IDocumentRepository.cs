namespace AwsRagChat.Application.Interfaces;

public sealed class ExistingDocumentRecord
{
    public string DocumentId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;

    [System.Obsolete("Use StorageKey instead.")]
    public string S3Key
    {
        get => StorageKey;
        set => StorageKey = value;
    }
    public string FileHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public int ChunkCount { get; set; }

    public int PageCount { get; set; }

    public bool IsAdminDocument { get; set; } = false;

    public List<string> AllowedRoles { get; set; } = [];

    public bool IsSearchable =>
        Status.Equals("INDEXED", StringComparison.OrdinalIgnoreCase);
}

public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];

    public string? NextToken { get; set; }
}

public sealed class DocumentStatsSnapshot
{
    public int TotalDocuments { get; set; }

    public int IndexedDocuments { get; set; }

    public int FailedDocuments { get; set; }

    public int UploadedDocuments { get; set; }

    public long TotalChunks { get; set; }

    public long TotalPages { get; set; }
}

public interface IDocumentRepository
{
    Task<ExistingDocumentRecord?> FindByOwnerAndHashAsync(
        string ownerUserId,
        string fileHash,
        CancellationToken cancellationToken = default);

    Task<ExistingDocumentRecord?> GetDocumentByIdAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    Task CreateUploadRecordAsync(
        string documentId,
        string ownerUserId,
        string fileName,
        string storageKey,
        string fileHash,
        long fileSizeBytes,
        bool isAdminDocument,
        IReadOnlyList<string> allowedRoles,
        CancellationToken cancellationToken = default);

    Task<int> GetDocumentCountAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<List<ExistingDocumentRecord>> GetDocumentsByUserAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        string documentId,
        string status,
        CancellationToken cancellationToken = default);

    Task MarkIndexedAsync(
        string documentId,
        int chunkCount,
        int pageCount,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    Task<List<ExistingDocumentRecord>> GetRecentDocumentsAsync(
    int take = 50,
    CancellationToken cancellationToken = default);

    Task<PagedResult<ExistingDocumentRecord>> GetDocumentMetadataPageAsync(
        int pageSize = 20,
        string? nextToken = null,
        CancellationToken cancellationToken = default);

    Task<DocumentStatsSnapshot> GetDocumentStatsSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetTotalDocumentCountAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetDocumentCountByStatusAsync(
        string status,
        CancellationToken cancellationToken = default);

    Task<long> GetTotalChunkCountAsync(
        CancellationToken cancellationToken = default);

    Task<long> GetTotalPageCountAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExistingDocumentRecord>> GetAdminDocumentsAsync(
    CancellationToken cancellationToken = default);
}
