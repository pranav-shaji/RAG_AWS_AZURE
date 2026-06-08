using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.AI.DocumentIntelligence;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Ingestion.Options;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ApplicationExtractedDocument = AwsRagChat.Application.Models.ExtractedDocument;
using IngestionExtractedDocument = AwsRagChat.Ingestion.Services.ExtractedDocument;


namespace AwsRagChat.Ingestion.Azure;

public sealed class AzureDocumentProcessor : IDocumentProcessor
{
    private readonly TextExtractionService _textExtractionService;
    private readonly DocumentIntelligenceClient _client;
    private readonly AzureDocumentProcessingOptions _options;
    private readonly IStorageProvider _storageProvider;

    public AzureDocumentProcessor(
        TextExtractionService textExtractionService,
        IOptions<AzureDocumentProcessingOptions> options,
        IStorageProvider storageProvider)
    {
        _textExtractionService = textExtractionService;
        _options = options.Value;
        _storageProvider = storageProvider;

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new InvalidOperationException("Azure AI Document Intelligence Endpoint is missing.");

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new DocumentIntelligenceClient(new Uri(_options.Endpoint), new AzureKeyCredential(_options.ApiKey));
        }
        else
        {
            _client = new DocumentIntelligenceClient(new Uri(_options.Endpoint), new DefaultAzureCredential());
        }
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

        // 1. Attempt direct extraction through TextExtractionService first
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

            // 2. Fallback to Azure Document Intelligence if needed (e.g. scanned PDF check)
            if (_textExtractionService.ShouldFallbackToOcr(request.FileName, extracted))
            {
                // Reset stream/re-open read stream since direct extraction consumed it
                var ocrStorageObject = await _storageProvider.OpenReadAsync(
                    new StorageObjectReadRequest
                    {
                        BucketOrContainerName = request.BucketOrContainerName,
                        ObjectKey = request.ObjectKey
                    },
                    cancellationToken);

                await using var ocrContent = ocrStorageObject.Content;
                using var ms = new MemoryStream();
                await ocrContent.CopyToAsync(ms, cancellationToken);
                ms.Position = 0;

                // Asynchronous OCR start for multi-cloud parity
                var operation = await _client.AnalyzeDocumentAsync(
                    WaitUntil.Started,
                    _options.ModelId,
                    BinaryData.FromStream(ms),
                    cancellationToken: cancellationToken);

                return new DocumentProcessingResult
                {
                    Status = DocumentProcessingStatus.OcrStarted,
                    OcrJobId = operation.Id,
                    Message = "Fallback to Azure Document Intelligence async OCR."
                };
            }

            return new DocumentProcessingResult
            {
                Status = DocumentProcessingStatus.Extracted,
                ExtractedDocument = MapExtractedDocument(extracted)
            };
        }

        // 3. Document Intelligence OCR fallback for files that cannot be read directly (like images)
        if (IsImageFile(request.FileName) || Path.GetExtension(request.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var storageObject = await _storageProvider.OpenReadAsync(
                new StorageObjectReadRequest
                {
                    BucketOrContainerName = request.BucketOrContainerName,
                    ObjectKey = request.ObjectKey
                },
                cancellationToken);

            await using var content = storageObject.Content;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;

            // Synchronous extraction compatibility
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _options.ModelId,
                BinaryData.FromStream(ms),
                cancellationToken: cancellationToken);

            var analyzeResult = operation.Value;

            return new DocumentProcessingResult
            {
                Status = DocumentProcessingStatus.Extracted,
                ExtractedDocument = MapAnalyzeResult(analyzeResult)
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

        var pipelineRequest = _client.Pipeline.CreateRequest();
        pipelineRequest.Method = RequestMethod.Get;
        
        var uriString = $"{_options.Endpoint.TrimEnd('/')}/documentintelligence/documentModels/{Uri.EscapeDataString(_options.ModelId)}/analyzeResults/{Uri.EscapeDataString(request.OcrJobId)}?api-version=2024-11-30";
        pipelineRequest.Uri.Reset(new Uri(uriString));

        var response = await _client.Pipeline.SendRequestAsync(pipelineRequest, cancellationToken);
        if (response.Status != 200)
        {
            throw new RequestFailedException(response);
        }

        if (response.ContentStream == null)
        {
            throw new InvalidOperationException("Response content stream is null.");
        }

        using var doc = JsonDocument.Parse(response.ContentStream);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString();
        
        if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Azure Document Intelligence operation is not succeeded. Status: {status}");
        }

        var analyzeResultElement = root.GetProperty("analyzeResult");
        var jsonText = analyzeResultElement.GetRawText();
        var analyzeResult = JsonSerializer.Deserialize<AnalyzeResult>(jsonText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (analyzeResult == null)
            throw new InvalidOperationException("Failed to deserialize AnalyzeResult from the response.");

        return new DocumentProcessingResult
        {
            Status = DocumentProcessingStatus.Extracted,
            ExtractedDocument = MapAnalyzeResult(analyzeResult)
        };
    }


    private static bool IsImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
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

    private static ApplicationExtractedDocument MapAnalyzeResult(AnalyzeResult analyzeResult)
    {
        var pages = new List<ExtractedDocumentPage>();

        foreach (var page in analyzeResult.Pages)
        {
            var pageText = string.Join(Environment.NewLine, page.Lines.Select(line => line.Content));
            pages.Add(new ExtractedDocumentPage
            {
                PageNumber = page.PageNumber,
                Text = pageText
            });
        }

        var fullText = string.Join(Environment.NewLine + Environment.NewLine, pages.Select(p => p.Text));

        return new ApplicationExtractedDocument
        {
            FullText = fullText,
            PageCount = pages.Count,
            Pages = pages
        };
    }
}
