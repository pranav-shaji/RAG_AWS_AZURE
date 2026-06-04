namespace AwsRagChat.Application.Models;

public sealed class ExtractedDocument
{
    public string FullText { get; init; } = string.Empty;

    public int PageCount { get; init; }

    public IReadOnlyList<ExtractedDocumentPage> Pages { get; init; } = [];
}

public sealed class ExtractedDocumentPage
{
    public int PageNumber { get; init; }

    public string Text { get; init; } = string.Empty;
}
