using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Services;
using Polly;
using Polly.Registry;
using ApplicationExtractedDocument = AwsRagChat.Application.Models.ExtractedDocument;
using IngestionExtractedDocument = AwsRagChat.Ingestion.Services.ExtractedDocument;

namespace AwsRagChat.Ingestion.Aws;

public sealed class AwsDocumentProcessor : IDocumentProcessor
{
    private readonly TextExtractionService _textExtractionService;
    private readonly TextractTextExtractionService _textractTextExtractionService;
    private readonly TextractAsyncExtractionService _textractAsyncExtractionService;
    private readonly IStorageProvider _storageProvider;
    private readonly ResiliencePipeline _resiliencePipeline;

    public AwsDocumentProcessor(
        TextExtractionService textExtractionService,
        TextractTextExtractionService textractTextExtractionService,
        TextractAsyncExtractionService textractAsyncExtractionService,
        IStorageProvider storageProvider,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _textExtractionService = textExtractionService;
        _textractTextExtractionService = textractTextExtractionService;
        _textractAsyncExtractionService = textractAsyncExtractionService;
        _storageProvider = storageProvider;
        _resiliencePipeline = pipelineProvider.GetPipeline("OcrPipeline");
    }

    public async Task<DocumentProcessingResult> ExtractAsync(
        DocumentProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.BucketOrContainerName))
            throw new ArgumentException("Bucket or container name is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.ObjectKey))
            throw new ArgumentException("Object key is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.FileName))
            throw new ArgumentException("File name is required.", nameof(request));

        if (_textExtractionService.CanExtractDirectly(request.FileName))
        {
            var storageObject = await _storageProvider.OpenReadAsync(
                new StorageObjectReadRequest
                {
                    BucketOrContainerName = request.BucketOrContainerName,
                    ObjectKey = request.ObjectKey
                },
                cancellationToken);

            await using var content = storageObject.Content;

            var extracted = await _textExtractionService.ExtractAsync(
                request.FileName,
                content,
                cancellationToken);

            if (_textExtractionService.ShouldFallbackToOcr(request.FileName, extracted))
            {
                var jobId = await _resiliencePipeline.ExecuteAsync(
                    async token => await _textractAsyncExtractionService.StartDocumentTextDetectionAsync(
                        request.BucketOrContainerName,
                        request.ObjectKey,
                        token),
                    cancellationToken);

                return new DocumentProcessingResult
                {
                    Status = DocumentProcessingStatus.OcrStarted,
                    OcrJobId = jobId,
                    Message = "Fallback to Textract async OCR."
                };
            }

            return new DocumentProcessingResult
            {
                Status = DocumentProcessingStatus.Extracted,
                ExtractedDocument = MapExtractedDocument(extracted)
            };
        }

        if (_textractTextExtractionService.CanExtractWithTextract(request.FileName))
        {
            var extracted = await _resiliencePipeline.ExecuteAsync(
                async token => await _textractTextExtractionService.ExtractFromS3Async(
                    request.BucketOrContainerName,
                    request.ObjectKey,
                    request.FileName,
                    token),
                cancellationToken);

            return new DocumentProcessingResult
            {
                Status = DocumentProcessingStatus.Extracted,
                ExtractedDocument = MapExtractedDocument(extracted)
            };
        }

        return new DocumentProcessingResult
        {
            Status = DocumentProcessingStatus.Unsupported,
            Message = $"File type '{Path.GetExtension(request.FileName)}' is not supported."
        };
    }

    public async Task<DocumentProcessingResult> GetCompletedOcrResultAsync(
        CompletedOcrRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.OcrJobId))
            throw new ArgumentException("OCR job ID is required.", nameof(request));

        var extracted = await _resiliencePipeline.ExecuteAsync(
            async token => await _textractAsyncExtractionService.GetCompletedDocumentAsync(
                request.OcrJobId,
                token),
            cancellationToken);

        return new DocumentProcessingResult
        {
            Status = DocumentProcessingStatus.Extracted,
            ExtractedDocument = MapExtractedDocument(extracted)
        };
    }

    private static ApplicationExtractedDocument MapExtractedDocument(IngestionExtractedDocument extractedDocument)
    {
        return new ApplicationExtractedDocument
        {
            FullText = extractedDocument.FullText,
            PageCount = extractedDocument.PageCount,
            Pages = extractedDocument.Pages
                .Select(page => new ExtractedDocumentPage
                {
                    PageNumber = page.PageNumber,
                    Text = page.Text
                })
                .ToList()
        };
    }
}
