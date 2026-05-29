using AwsRagChat.Domain.Entities;
using AwsRagChat.Application.Interfaces;

namespace AwsRagChat.Application.DTOs;

public class ChatAskRequest
{
    public string SessionId { get; set; } = default!;

    public string? DocumentId { get; set; }

    public bool SearchAcrossAllDocuments { get; set; }

    public string Question { get; set; } = default!;

    public string OutputFormat { get; set; } = "text";
}

public sealed class ChartData
{
    public List<string> Labels { get; set; } = [];
    public List<int> Values { get; set; } = [];
}

public sealed class TableData
{
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
}

public sealed class InteractiveOptionDto
{
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
}

public sealed class InteractiveOptionsData
{
    public List<InteractiveOptionDto> Options { get; set; } = [];
}

public sealed class DocumentSelectorData
{
    public List<ExistingDocumentRecord> Documents { get; set; } = [];
    public string? SelectedDocumentId { get; set; }
}

public sealed class DocumentImageDto
{
    public string DocumentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string SourceType { get; set; } = string.Empty;
}

public class ChatAskResponse
{
    public string ResponseType { get; set; } = "text";

    public string Answer { get; set; } = string.Empty;

    public List<Citation> Citations { get; set; } = [];

    public object? Data { get; set; }

    public ChartData? ChartData { get; set; }
}
