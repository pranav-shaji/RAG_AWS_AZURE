using Amazon.Textract;
using Amazon.Textract.Model;
using System.Text;

namespace AwsRagChat.Ingestion.Services;

public sealed class TextractTextExtractionService
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".pdf",
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff"
    ];

    private readonly IAmazonTextract _amazonTextract;

    public TextractTextExtractionService(IAmazonTextract amazonTextract)
    {
        _amazonTextract = amazonTextract;
    }

    public bool CanExtractWithTextract(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return SupportedExtensions.Contains(extension);
    }

    public async Task<ExtractedDocument> ExtractFromS3Async(
        string bucketName,
        string objectKey,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
            throw new ArgumentException("Bucket name is required.", nameof(bucketName));

        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key is required.", nameof(objectKey));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));

        if (!CanExtractWithTextract(fileName))
            throw new NotSupportedException($"Textract OCR does not support file '{fileName}'.");

        var request = new DetectDocumentTextRequest
        {
            Document = new Document
            {
                S3Object = new Amazon.Textract.Model.S3Object
                {
                    Bucket = bucketName,
                    Name = objectKey
                }
            }
        };

        var response = await _amazonTextract.DetectDocumentTextAsync(request, cancellationToken);

        var pageCount = response.Blocks
            .Where(b => b.BlockType == BlockType.PAGE)
            .Select(b => b.Page ?? 0)
            .Where(page => page > 0)
            .DefaultIfEmpty(1)
            .Max();

        var pages = response.Blocks
            .Where(b => b.BlockType == BlockType.LINE && !string.IsNullOrWhiteSpace(b.Text))
            .GroupBy(b => b.Page ?? 1)
            .OrderBy(g => g.Key)
            .Select(g => new ExtractedPage
            {
                PageNumber = g.Key,
                Text = TextExtractionService.NormalizeExtractedText(
                    string.Join(Environment.NewLine, g.Select(x => x.Text)))
            })
            .ToList();

        var fullTextBuilder = new StringBuilder();

        foreach (var page in pages)
        {
            if (!string.IsNullOrWhiteSpace(page.Text))
            {
                fullTextBuilder.AppendLine(page.Text);
            }
        }

        return new ExtractedDocument
        {
            FullText = TextExtractionService.NormalizeExtractedText(fullTextBuilder.ToString()),
            PageCount = Math.Max(pageCount, pages.Count),
            Pages = pages
        };
    }
}
