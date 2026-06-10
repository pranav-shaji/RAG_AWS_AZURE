using Amazon.Textract;
using Amazon.Textract.Model;
using AwsRagChat.Ingestion.Options;
using Microsoft.Extensions.Options;
using System.Text;

namespace AwsRagChat.Ingestion.Services;

public sealed class TextractAsyncExtractionService
{
    private readonly IAmazonTextract _amazonTextract;
    private readonly TextractAsyncOptions _textractAsyncOptions;

    public TextractAsyncExtractionService(
        IAmazonTextract amazonTextract,
        IOptions<TextractAsyncOptions> textractAsyncOptions)
    {
        _amazonTextract = amazonTextract;
        _textractAsyncOptions = textractAsyncOptions.Value;
    }

    public async Task<string> StartDocumentTextDetectionAsync(
        string bucketName,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
            throw new ArgumentException("Bucket name is required.", nameof(bucketName));

        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key is required.", nameof(objectKey));

        if (string.IsNullOrWhiteSpace(_textractAsyncOptions.SnsTopicArn))
            throw new InvalidOperationException("Textract async SNS topic ARN is not configured.");

        if (string.IsNullOrWhiteSpace(_textractAsyncOptions.TextractPublishRoleArn))
            throw new InvalidOperationException("Textract publish role ARN is not configured.");

        var jobTag = System.Diagnostics.Activity.Current?.Id ?? _textractAsyncOptions.JobTag;
        if (!string.IsNullOrEmpty(jobTag) && jobTag.Length > 64)
        {
            jobTag = jobTag[..64];
        }

        var request = new StartDocumentTextDetectionRequest
        {
            DocumentLocation = new DocumentLocation
            {
                S3Object = new Amazon.Textract.Model.S3Object
                {
                    Bucket = bucketName,
                    Name = objectKey
                }
            },
            NotificationChannel = new NotificationChannel
            {
                RoleArn = _textractAsyncOptions.TextractPublishRoleArn,
                SNSTopicArn = _textractAsyncOptions.SnsTopicArn
            },
            JobTag = jobTag
        };

        var response = await _amazonTextract.StartDocumentTextDetectionAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(response.JobId))
            throw new InvalidOperationException("Textract did not return a JobId.");

        return response.JobId;
    }

    public async Task<ExtractedDocument> GetCompletedDocumentAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("JobId is required.", nameof(jobId));

        var allBlocks = new List<Block>();
        string? nextToken = null;

        do
        {
            var request = new GetDocumentTextDetectionRequest
            {
                JobId = jobId,
                NextToken = nextToken
            };

            var response = await _amazonTextract.GetDocumentTextDetectionAsync(request, cancellationToken);

            if (response.JobStatus == JobStatus.FAILED)
            {
                throw new InvalidOperationException(
                    $"Textract async job '{jobId}' failed with status FAILED.");
            }

            if (response.JobStatus == JobStatus.PARTIAL_SUCCESS)
            {
                throw new InvalidOperationException(
                    $"Textract async job '{jobId}' returned PARTIAL_SUCCESS, which is not handled in this pipeline.");
            }

            if (response.Blocks is { Count: > 0 })
                allBlocks.AddRange(response.Blocks);

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        var pageCount = allBlocks
            .Where(b => b.BlockType == BlockType.PAGE)
            .Select(b => b.Page ?? 0)
            .Where(page => page > 0)
            .DefaultIfEmpty(1)
            .Max();

        var pages = allBlocks
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
                fullTextBuilder.AppendLine(page.Text);
        }

        return new ExtractedDocument
        {
            FullText = TextExtractionService.NormalizeExtractedText(fullTextBuilder.ToString()),
            PageCount = Math.Max(pageCount, pages.Count),
            Pages = pages
        };
    }
}
