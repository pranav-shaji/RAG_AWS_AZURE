# Phase 5 Commit 5 Plan: Azure AI Document Intelligence Integration

This document outlines the design and implementation details for integrating Azure AI Document Intelligence as the document processor swap for AWS Textract.

---

## 1. Files to Create

*   **`AwsRagChat.Ingestion/Options/AzureDocumentProcessingOptions.cs`**:
    *   Holds configuration settings for `Endpoint`, `ApiKey`, and `ModelId` (defaults to `"prebuilt-layout"` which handles OCR, read order, and tables).
*   **`AwsRagChat.Ingestion/Azure/AzureDocumentProcessor.cs`**:
    *   Implements `IDocumentProcessor` using the `Azure.AI.DocumentIntelligence` SDK.
*   **`AwsRagChat.Ingestion/Azure/AzureIngestionComposition.cs`**:
    *   Azure Composition class (analogous to `AwsIngestionComposition.cs`) to wire up storage, Cosmos DB repositories, Azure OpenAI, Azure AI Search, and the Document Intelligence processor.

---

## 2. Files to Modify

*   **`AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj`**:
    *   Add package reference for `Azure.AI.DocumentIntelligence`.

---

## 3. Azure SDK Packages Required

We will add the following NuGet package:
*   `Azure.AI.DocumentIntelligence` (v1.0.0-beta.2 or latest stable version).

---

## 4. Mapping: AWS Textract vs Azure Document Intelligence

| Feature | AWS Textract | Azure AI Document Intelligence |
| :--- | :--- | :--- |
| **SDK Client** | `AmazonTextractClient` | `DocumentIntelligenceClient` |
| **Authentication** | AWSSDK standard credential resolution | `AzureKeyCredential` (API key) or `DefaultAzureCredential` |
| **Model ModelId** | Standard text extraction API endpoint | `"prebuilt-layout"` or `"prebuilt-read"` |
| **Input Source** | Stream bytes or S3 object pointer | Stream bytes or Blob container URL pointer |
| **Response Blocks** | Blocks of types `PAGE`, `LINE`, `WORD` with geometry | `AnalyzeResult` with `Pages`, `Lines`, `Paragraphs`, and `Tables` |
| **Page Aggregation** | Collects text blocks and groups by Page metadata | Groups naturally under `AnalyzeResult.Pages` collections |

---

## 5. OCR Completion Flow Compatibility

AWS Textract relies on decoupled asynchronous workflow triggers (Textract publishes job status updates to SNS, which routes to SQS and triggers the `TextractCompletionFunction` Lambda). 

To map this behavior into the Azure ecosystem, we support the following architecture options:
1.  **Durable Functions Orchestration (Recommended for complex workflows)**: A Durable Function initiates the analysis operation (`WaitUntil.Started`), records the `OcrJobId` (which maps to `operation.Id`), uses a durable timer to poll status, and invokes the ingestion pipeline upon completion.
2.  **Queue Polling Trigger**: A lightweight trigger writes the `OcrJobId` to an Azure Storage Queue. A queue-triggered function polls status incrementally and finishes execution when complete.
3.  **Synchronous In-Function Polling (Best for small/medium documents)**: Because Azure Document Intelligence operations are generally fast (5-15 seconds), the Event Grid trigger function can poll the status directly using `operation.WaitForCompletionAsync()` within a standard execution timeout window.

---

## 6. Sync Extraction Compatibility

*   *AWS Behavior*: Direct sync parsing uses the localized C# PDF/text parser `TextExtractionService`. If fallback or direct Textract is used for small documents, it calls the synchronous `DetectDocumentText` client method.
*   *Azure Behavior*: The `AzureDocumentProcessor.ExtractAsync` method will first attempt direct text extraction via `TextExtractionService`. If fallback is needed, it calls:
    ```csharp
    var operation = await _client.AnalyzeDocumentAsync(
        WaitUntil.Completed, 
        _options.ModelId, 
        BinaryData.FromStream(stream));
    ```
    This blocks until complete and directly returns the extracted pages.

---

## 7. Async Extraction Compatibility

For large, multi-page PDFs that cannot be processed in a single synchronous request:
*   *AWS Behavior*: `StartDocumentTextDetectionAsync` starts the job and returns a `JobId`.
*   *Azure Behavior*: We initiate document analysis asynchronously using the `WaitUntil.Started` flag:
    ```csharp
    var operation = await _client.AnalyzeDocumentAsync(
        WaitUntil.Started, 
        _options.ModelId, 
        BinaryData.FromStream(stream));
    
    return new DocumentProcessingResult
    {
        Status = DocumentProcessingStatus.OcrStarted,
        OcrJobId = operation.Id,
        Message = "Azure Document Intelligence async OCR started."
    };
    ```
*   *Resolving Completed Results*: When the background trigger processes completion using `GetCompletedOcrResultAsync(CompletedOcrRequest)`:
    ```csharp
    var operation = _client.GetAnalyzeDocumentOperation(_options.ModelId, request.OcrJobId);
    await operation.WaitForCompletionAsync(cancellationToken);
    
    var analyzeResult = operation.Value;
    // Map pages and lines back to ExtractedDocument format
    ```

---

## 8. Build Verification Plan

1.  **Add package reference**:
    ```powershell
    dotnet add AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj package Azure.AI.DocumentIntelligence --prerelease
    ```
2.  **Verify project compile**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj
    ```
3.  **Validate full solution**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.slnx
    ```
