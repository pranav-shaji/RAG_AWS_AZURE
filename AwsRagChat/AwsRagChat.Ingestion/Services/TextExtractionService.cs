using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace AwsRagChat.Ingestion.Services;

public sealed class TextExtractionService
{
    private static readonly HashSet<string> DirectExtractionExtensions =
    [
        ".txt",
        ".csv",
        ".pdf"
    ];

    public bool CanExtractDirectly(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return DirectExtractionExtensions.Contains(extension);
    }

    public bool ShouldFallbackToOcr(string fileName, ExtractedDocument extractedDocument)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (extractedDocument is null)
            throw new ArgumentNullException(nameof(extractedDocument));

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension != ".pdf")
            return false;

        if (extractedDocument.Pages.Count == 0)
            return true;

        var fullText = extractedDocument.FullText?.Trim() ?? string.Empty;

        if (fullText.Length >= 100)
            return false;

        var nonEmptyPages = extractedDocument.Pages.Count(p => !string.IsNullOrWhiteSpace(p.Text));
        if (nonEmptyPages == 0)
            return true;

        var totalPageTextLength = extractedDocument.Pages.Sum(p => (p.Text ?? string.Empty).Trim().Length);

        return totalPageTextLength < 100;
    }

    public async Task<ExtractedDocument> ExtractAsync(
        string fileName,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));

        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".txt" => await ReadPlainTextAsync(stream, cancellationToken),
            ".csv" => await ReadPlainTextAsync(stream, cancellationToken),
            ".pdf" => await ReadPdfTextAsync(stream, cancellationToken),
            _ => throw new NotSupportedException($"File type '{extension}' is not supported for direct extraction.")
        };
    }

    public static string NormalizeExtractedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\u00a0", " ");

        normalized = Regex.Replace(normalized, @"(?<=\w)-\n(?=\w)", string.Empty);
        normalized = Regex.Replace(normalized, @"[ \t]+", " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");

        return normalized.Trim();
    }

    private static async Task<ExtractedDocument> ReadPlainTextAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
            stream.Position = 0;

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var content = NormalizeExtractedText(await reader.ReadToEndAsync(cancellationToken));

        return new ExtractedDocument
        {
            FullText = content,
            PageCount = 1,
            Pages =
            [
                new ExtractedPage
                {
                    PageNumber = 1,
                    Text = content
                }
            ]
        };
    }

    private static async Task<ExtractedDocument> ReadPdfTextAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (stream.CanSeek)
            stream.Position = 0;

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        using var document = PdfDocument.Open(memoryStream);
        var pageCount = document.NumberOfPages;

        var pages = new List<ExtractedPage>();
        var fullTextBuilder = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = BuildReadablePdfPageText(page);

            if (!string.IsNullOrWhiteSpace(text))
            {
                pages.Add(new ExtractedPage
                {
                    PageNumber = page.Number,
                    Text = text
                });

                fullTextBuilder.AppendLine(text);
            }
        }

        return new ExtractedDocument
        {
            FullText = NormalizeExtractedText(fullTextBuilder.ToString()),
            PageCount = pageCount,
            Pages = pages
        };
    }

    private static string BuildReadablePdfPageText(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToList();

        if (words.Count == 0)
            return NormalizeExtractedText(page.Text ?? string.Empty);

        var lines = words
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom, 1))
            .OrderByDescending(group => group.Key)
            .Select(group => string.Join(" ", group.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text)))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return NormalizeExtractedText(string.Join(Environment.NewLine, lines));
    }
}

public sealed class ExtractedDocument
{
    public string FullText { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public List<ExtractedPage> Pages { get; set; } = [];
}

public sealed class ExtractedPage
{
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
}
