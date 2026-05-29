using AwsRagChat.Domain.Entities;
using System.Text;
using System.Text.RegularExpressions;

namespace AwsRagChat.Ingestion.Services;

public sealed class ChunkingService
{
    private static readonly Regex HeadingRegex = new(
        @"^\s*((?:[IVXLCDM]+|[A-Z]|\d+(?:\.\d+)*)[\).]?\s+)?[A-Z][A-Z0-9,&:/\-() ]{5,}\s*$",
        RegexOptions.Compiled);

    public List<DocumentChunk> CreateChunks(
        string documentId,
        string fileName,
        string s3Key,
        ExtractedDocument extractedDocument,
        int maxChunkLength = 1200,
        int overlapLength = 180,
        int minChunkLength = 500)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("DocumentId is required.", nameof(documentId));

        if (extractedDocument is null)
            throw new ArgumentNullException(nameof(extractedDocument));

        var chunks = new List<DocumentChunk>();

        if (extractedDocument.Pages.Count == 0)
            return chunks;

        var chunkOrder = 1;
        string currentHeading = "Document";

        foreach (var page in extractedDocument.Pages.OrderBy(x => x.PageNumber))
        {
            var normalized = TextExtractionService.NormalizeExtractedText(page.Text);

            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            var blocks = BuildSemanticBlocks(normalized, ref currentHeading);
            var pageChunks = BuildChunksFromBlocks(blocks, maxChunkLength, overlapLength, minChunkLength);

            foreach (var chunk in pageChunks)
            {
                var heading = string.IsNullOrWhiteSpace(chunk.Heading)
                    ? $"Page {page.PageNumber}"
                    : chunk.Heading;

                chunks.Add(new DocumentChunk
                {
                    DocumentId = documentId,
                    ChunkId = $"chunk-{chunkOrder:D4}",
                    FileName = fileName,
                    S3Key = s3Key,
                    PageNumber = page.PageNumber,
                    Section = heading,
                    Heading = heading,
                    ChunkOrder = chunkOrder,
                    Text = chunk.Text,
                    CreatedAtUtc = DateTime.UtcNow
                });

                chunkOrder++;
            }
        }

        return chunks;
    }

    private static List<SemanticBlock> BuildSemanticBlocks(
        string pageText,
        ref string currentHeading)
    {
        var blocks = new List<SemanticBlock>();
        var paragraph = new StringBuilder();

        foreach (var rawLine in pageText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = NormalizeLine(rawLine);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsHeading(line))
            {
                FlushParagraph(blocks, paragraph, currentHeading);
                currentHeading = NormalizeHeading(line);
                blocks.Add(new SemanticBlock(currentHeading, currentHeading, IsHeading: true));
                continue;
            }

            if (paragraph.Length > 0 && StartsNewParagraph(line))
            {
                FlushParagraph(blocks, paragraph, currentHeading);
            }

            if (paragraph.Length > 0)
                paragraph.Append(' ');

            paragraph.Append(line);
        }

        FlushParagraph(blocks, paragraph, currentHeading);
        return blocks;
    }

    private static List<SemanticChunk> BuildChunksFromBlocks(
        IReadOnlyList<SemanticBlock> blocks,
        int maxChunkLength,
        int overlapLength,
        int minChunkLength)
    {
        var chunks = new List<SemanticChunk>();
        var current = new StringBuilder();
        var currentHeading = string.Empty;
        var pendingHeading = string.Empty;

        foreach (var block in blocks)
        {
            if (block.IsHeading)
            {
                pendingHeading = block.Heading;

                if (current.Length == 0)
                {
                    currentHeading = block.Heading;
                    current.Append(block.Text);
                }

                continue;
            }

            var textToAppend = block.Text;

            if (!string.IsNullOrWhiteSpace(pendingHeading) &&
                !current.ToString().Contains(pendingHeading, StringComparison.OrdinalIgnoreCase))
            {
                textToAppend = $"{pendingHeading}{Environment.NewLine}{Environment.NewLine}{textToAppend}";
            }

            var proposedLength = current.Length == 0
                ? textToAppend.Length
                : current.Length + 2 + textToAppend.Length;

            if (proposedLength <= maxChunkLength || current.Length < minChunkLength)
            {
                AppendBlock(current, textToAppend);
                currentHeading = string.IsNullOrWhiteSpace(pendingHeading) ? block.Heading : pendingHeading;
                pendingHeading = string.Empty;
                continue;
            }

            FlushChunk(chunks, current, currentHeading);

            var overlap = GetOverlapText(chunks.LastOrDefault()?.Text, overlapLength);
            currentHeading = string.IsNullOrWhiteSpace(pendingHeading) ? block.Heading : pendingHeading;

            if (!string.IsNullOrWhiteSpace(currentHeading))
                AppendBlock(current, currentHeading);

            if (!string.IsNullOrWhiteSpace(overlap))
                AppendBlock(current, overlap);

            AppendBlock(current, block.Text);
            pendingHeading = string.Empty;
        }

        FlushChunk(chunks, current, currentHeading);

        return chunks
            .SelectMany(chunk => SplitOversizedChunk(chunk, maxChunkLength, overlapLength))
            .ToList();
    }

    private static IEnumerable<SemanticChunk> SplitOversizedChunk(
        SemanticChunk chunk,
        int maxChunkLength,
        int overlapLength)
    {
        if (chunk.Text.Length <= maxChunkLength)
            return [chunk];

        var results = new List<SemanticChunk>();
        var sentences = SplitIntoSentences(chunk.Text);
        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var proposedLength = current.Length == 0
                ? sentence.Length
                : current.Length + 1 + sentence.Length;

            if (proposedLength <= maxChunkLength)
            {
                if (current.Length > 0)
                    current.Append(' ');

                current.Append(sentence);
                continue;
            }

            FlushChunk(results, current, chunk.Heading);

            var overlap = GetOverlapText(results.LastOrDefault()?.Text, overlapLength);
            if (!string.IsNullOrWhiteSpace(chunk.Heading))
                AppendBlock(current, chunk.Heading);
            if (!string.IsNullOrWhiteSpace(overlap))
                AppendBlock(current, overlap);

            AppendBlock(current, sentence);
        }

        FlushChunk(results, current, chunk.Heading);
        return results;
    }

    private static bool IsHeading(string line)
    {
        if (line.Length is < 6 or > 120)
            return false;

        if (line.Count(char.IsLetter) < 3)
            return false;

        if (HeadingRegex.IsMatch(line))
            return true;

        return Regex.IsMatch(line, @"^\s*(?:[IVXLCDM]+|\d+(?:\.\d+)*|[A-Z])[\).]?\s+[A-Z][A-Za-z0-9,&:/\-() ]{4,}$") &&
               line.Count(char.IsLower) <= Math.Max(2, line.Length / 5);
    }

    private static string NormalizeHeading(string heading)
    {
        return Regex.Replace(heading.Trim(), @"\s+", " ");
    }

    private static string NormalizeLine(string line)
    {
        return Regex.Replace(line.Trim(), @"\s+", " ");
    }

    private static bool StartsNewParagraph(string line)
    {
        return Regex.IsMatch(line, @"^\s*(?:[-*•]|\d+[\).])\s+") ||
               Regex.IsMatch(line, @"^[A-Z][^.!?]{0,90}:$");
    }

    private static void FlushParagraph(
        List<SemanticBlock> blocks,
        StringBuilder paragraph,
        string heading)
    {
        var text = paragraph.ToString().Trim();

        if (!string.IsNullOrWhiteSpace(text))
            blocks.Add(new SemanticBlock(heading, text, IsHeading: false));

        paragraph.Clear();
    }

    private static void AppendBlock(StringBuilder builder, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();

        builder.Append(text.Trim());
    }

    private static void FlushChunk(
        List<SemanticChunk> chunks,
        StringBuilder current,
        string heading)
    {
        var text = current.ToString().Trim();

        if (!string.IsNullOrWhiteSpace(text))
            chunks.Add(new SemanticChunk(heading, text));

        current.Clear();
    }

    private static List<string> SplitIntoSentences(string text)
    {
        return Regex
            .Split(text, @"(?<=[.!?])\s+")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static string GetOverlapText(string? previousChunk, int overlapLength)
    {
        if (string.IsNullOrWhiteSpace(previousChunk))
            return string.Empty;

        if (previousChunk.Length <= overlapLength)
            return previousChunk;

        var start = Math.Max(0, previousChunk.Length - overlapLength);
        var overlap = previousChunk[start..].Trim();
        var sentenceStart = overlap.IndexOfAny(['.', '!', '?']);

        return sentenceStart >= 0 && sentenceStart + 1 < overlap.Length
            ? overlap[(sentenceStart + 1)..].Trim()
            : overlap;
    }

    private sealed record SemanticBlock(string Heading, string Text, bool IsHeading);

    private sealed record SemanticChunk(string Heading, string Text);
}
