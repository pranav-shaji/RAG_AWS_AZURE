# Phase 3 Implementation Plan

## Objective

Implement Phase 3 by introducing document-processing and storage-read abstractions for ingestion while preserving current AWS behavior.

Phase 3 should make ingestion stop directly coordinating S3 stream reads and Textract extraction decisions inside Lambda handlers. The handlers should remain AWS event adapters with unchanged Lambda signatures, but extraction and document-read behavior should move behind provider-neutral contracts.

Phase 3 must not:

- Add Azure implementations.
- Change Lambda handler signatures.
- Rename `S3Key`.
- Change frontend/auth behavior.
- Introduce full Lambda dependency injection.
- Change S3/SQS/SNS/Textract/Bedrock/DynamoDB/OpenSearch runtime behavior.
- Implement Phase 4 identity or external contract cleanup.

The intended Phase 3 end state is:

```text
S3DocumentIngestionFunction
  -> parse S3 event
  -> resolve document metadata
  -> update status
  -> IDocumentProcessor.ExtractAsync(...)
  -> IIngestionPipeline.ProcessExtractedDocumentAsync(...)

TextractCompletionFunction
  -> parse SQS/SNS/Textract completion event
  -> resolve document metadata
  -> IDocumentProcessor.GetCompletedOcrResultAsync(...)
  -> IIngestionPipeline.ProcessExtractedDocumentAsync(...)
```

## Files Affected

### Application Layer

Expected files:

- `AwsRagChat.Application/Interfaces/IDocumentProcessor.cs`
- `AwsRagChat.Application/Interfaces/IStorageProvider.cs`

Optional model files if the implementation benefits from explicit request/result shapes:

- `AwsRagChat.Application/Models/DocumentProcessingRequest.cs`
- `AwsRagChat.Application/Models/DocumentProcessingResult.cs`
- `AwsRagChat.Application/Models/StorageObjectRequest.cs`
- `AwsRagChat.Application/Models/StorageObjectReadResult.cs`

Phase 3 should keep these abstractions provider-neutral and free from AWS SDK types.

### Infrastructure Layer

Likely affected files:

- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`
- `AwsRagChat.Infrastructure/DependencyInjection.cs`
- `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs`

Expected infrastructure work:

- Add read/open support to `S3StorageService` through `IStorageProvider`.
- Keep upload and read URL behavior unchanged.
- Register AWS-backed storage read behavior through existing provider-neutral registration.

### Ingestion Layer

Expected new files:

- `AwsRagChat.Ingestion/Aws/AwsDocumentProcessor.cs`

Likely affected files:

- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`
- `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`
- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`
- `AwsRagChat.Ingestion/Services/TextExtractionService.cs`
- `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs`
- `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs`

Expected ingestion work:

- Add an AWS-backed document processor that wraps current direct extraction, Textract sync OCR, and Textract async OCR behavior.
- Update `AwsIngestionComposition` to construct and expose the document processor.
- Update handlers to delegate extraction decisions to `IDocumentProcessor`.
- Preserve all current event parsing, status update calls, log messages where practical, and pipeline calls.

## Exact Abstractions Required

### `IDocumentProcessor`

Add a provider-neutral document processing abstraction in the application layer.

Purpose:

- Extract text/pages from a provider-backed document reference.
- Start OCR when async OCR is required.
- Retrieve completed async OCR results.
- Keep Textract-specific request/response models out of handlers and application contracts.

Recommended shape:

```text
IDocumentProcessor
  ExtractAsync(DocumentProcessingRequest request, CancellationToken cancellationToken)
  GetCompletedOcrResultAsync(CompletedOcrRequest request, CancellationToken cancellationToken)
```

Recommended request model:

```text
DocumentProcessingRequest
  BucketOrContainerName
  ObjectKey
  FileName
```

Recommended result model:

```text
DocumentProcessingResult
  Status
  ExtractedDocument?
  OcrJobId?
  Message?
```

Recommended status values:

```text
Extracted
OcrStarted
Unsupported
Failed
```

The result shape must allow `S3DocumentIngestionFunction` to preserve the existing behavior:

- Direct extraction succeeds and continues to indexing.
- Empty direct extraction for scanned PDFs starts Textract async OCR and returns a job ID.
- Supported image files use Textract sync OCR and continue to indexing.
- Unsupported file types throw or return a failure result equivalent to current behavior.

### `CompletedOcrRequest`

Add a provider-neutral request for completed OCR lookup.

Recommended shape:

```text
CompletedOcrRequest
  OcrJobId
  BucketOrContainerName
  ObjectKey
  FileName
```

The AWS implementation may only need `OcrJobId`, but including the object reference keeps the contract ready for providers that need object context.

### `IStorageProvider` Read/Open Capability

Extend the provider-neutral storage abstraction to support ingestion reads.

Current state:

```text
IStorageProvider : IStorageService
```

Recommended addition:

```text
OpenReadAsync(StorageObjectReadRequest request, CancellationToken cancellationToken)
```

Recommended request model:

```text
StorageObjectReadRequest
  BucketOrContainerName
  ObjectKey
```

Recommended result:

```text
StorageObjectReadResult
  Stream Content
  string? ContentType
  long? ContentLength
```

Important compatibility note:

- Do not remove or change existing `IStorageService` methods.
- Keep current upload/read URL semantics intact.
- The AWS implementation can map `BucketOrContainerName` to the S3 bucket and `ObjectKey` to the S3 key.

### `AwsDocumentProcessor`

Create an AWS-backed implementation of `IDocumentProcessor`.

It should wrap existing services:

- `TextExtractionService`
- `TextractTextExtractionService`
- `TextractAsyncExtractionService`
- `IStorageProvider` or `S3StorageService` read/open support

Expected AWS behavior:

- Use direct extraction for file types currently handled by `TextExtractionService`.
- Open the object stream through the storage abstraction instead of through handler-level `IAmazonS3`.
- Preserve fallback to Textract async OCR when direct extraction returns insufficient text.
- Preserve Textract sync OCR for supported image documents.
- Preserve Textract async job start and completed result retrieval.

### `AwsIngestionServices`

Update the ingestion service bundle to expose:

```text
IDocumentProcessor DocumentProcessor
```

After Phase 3, the service bundle should no longer need to expose:

- `IAmazonS3 AmazonS3`
- `TextExtractionService TextExtractionService`
- `TextractTextExtractionService TextractTextExtractionService`
- `TextractAsyncExtractionService TextractAsyncExtractionService`

This removal should happen only after handlers no longer depend on those members.

## Risks

### Medium-High: Async OCR Behavior Drift

The current S3 handler starts Textract async OCR only when direct extraction falls back. Moving that decision into `AwsDocumentProcessor` may accidentally change when OCR starts.

Mitigation:

- Preserve the current branch order exactly.
- Keep existing `TextExtractionService.ShouldFallbackToTextract` logic.
- Add focused tests or review checkpoints for direct PDF/text/CSV, scanned PDF fallback, supported images, and unsupported extensions.

### Medium: Stream Lifetime Handling

Storage read/open abstraction introduces stream ownership questions.

Mitigation:

- Make the caller that receives the stream responsible for disposing it.
- Ensure `AwsDocumentProcessor` disposes streams after direct extraction.
- Do not return disposed streams from storage result wrappers.

### Medium: Handler Status Update Sequencing

The handlers currently call status methods in specific places.

Mitigation:

- Keep status update calls in handlers during Phase 3.
- Let `IDocumentProcessor` return whether OCR was started instead of updating status itself.
- Preserve `MarkUploadedAsync`, `MarkProcessingAsync`, `MarkOcrStartedAsync`, `MarkFailedAsync`, and pipeline status behavior.

### Medium: Composition Drift

`AwsIngestionComposition` currently centralizes AWS clients and service construction. Adding document processor construction could accidentally change options or table names.

Mitigation:

- Preserve current options sources.
- Keep `DocumentStatusService` table name source unchanged.
- Build after each commit boundary.

### Medium: Interface Shape Overreach

Adding too much to `IDocumentProcessor` could prematurely encode AWS/Textract assumptions.

Mitigation:

- Keep contract terms neutral: object key, storage location, OCR job ID, extracted document.
- Avoid Textract, S3, SNS, SQS, Lambda, or AWS SDK types in application interfaces and models.
- Do not add Azure-specific concepts.

### Low-Medium: Runtime Construction Without Full DI

Phase 3 should still avoid full Lambda DI, but adding more abstractions may make manual composition more complex.

Mitigation:

- Continue using `AwsIngestionComposition`.
- Keep construction explicit and localized.
- Do not introduce `IServiceCollection` into Lambda handlers in Phase 3.

## Commit Boundaries

### Commit 1: Add Provider-Neutral Document Processing Contracts

Files:

- `AwsRagChat.Application/Interfaces/IDocumentProcessor.cs`
- Optional application model files for document processing requests/results.

Changes:

- Add provider-neutral document processor contract.
- Add request/result models if needed.
- No AWS SDK references.
- No handler changes.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Application\AwsRagChat.Application.csproj
```

Risk:

- Low

### Commit 2: Add Storage Read/Open Contract

Files:

- `AwsRagChat.Application/Interfaces/IStorageProvider.cs`
- Optional storage read request/result model files.
- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`

Changes:

- Add storage read/open capability to `IStorageProvider`.
- Implement it in `S3StorageService`.
- Preserve existing upload/read URL behavior.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Infrastructure\AwsRagChat.Infrastructure.csproj
```

Risk:

- Medium

### Commit 3: Add AWS Document Processor

Files:

- `AwsRagChat.Ingestion/Aws/AwsDocumentProcessor.cs`
- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`
- `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`

Changes:

- Implement `IDocumentProcessor` using existing direct extraction and Textract services.
- Construct it through `AwsIngestionComposition`.
- Expose it through `AwsIngestionServices`.
- Do not update handlers yet.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Medium

### Commit 4: Use Document Processor In S3 Handler

Files:

- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- Possibly `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`

Changes:

- Replace handler-level direct S3 stream read and extraction branching with `IDocumentProcessor`.
- Keep `FunctionHandler(S3Event, ILambdaContext)` unchanged.
- Keep S3 event parsing and document ID/owner extraction unchanged.
- Keep status update sequence unchanged.
- Preserve OCR-start behavior and continue semantics.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Medium-high

### Commit 5: Use Document Processor In Textract Completion Handler

Files:

- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`

Changes:

- Replace direct `TextractAsyncExtractionService.GetCompletedDocumentAsync` usage with `IDocumentProcessor.GetCompletedOcrResultAsync`.
- Keep `FunctionHandler(SQSEvent, ILambdaContext)` unchanged.
- Keep SQS/SNS parsing unchanged.
- Keep Textract completion message shape parsing unchanged.
- Keep failed/nonterminal status behavior unchanged.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Medium

### Commit 6: Remove Handler-Exposed Low-Level Services From Composition Bundle

Files:

- `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`
- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`
- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`

Changes:

- Remove `IAmazonS3`, `TextExtractionService`, `TextractTextExtractionService`, and `TextractAsyncExtractionService` from `AwsIngestionServices` if handlers no longer use them.
- Keep direct AWS client creation centralized in `AwsIngestionComposition`.
- Do not remove existing services from the composition helper if the new document processor still needs them internally.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Low-medium

### Commit 7: Final Phase 3 Verification

Files:

- No intended runtime code changes.
- Optional report file if requested separately.

Actions:

- Build application, infrastructure, ingestion, and API.
- Confirm no Azure code was added.
- Confirm Lambda handler signatures are unchanged.
- Confirm handlers no longer directly coordinate S3 stream reads or Textract extraction decisions.
- Confirm AWS behavior remains centralized behind AWS composition/provider implementations.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Application\AwsRagChat.Application.csproj
dotnet build AwsRagChat\AwsRagChat.Infrastructure\AwsRagChat.Infrastructure.csproj
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

Risk:

- Low

## Build Verification Strategy

Run targeted builds after each commit boundary:

```powershell
dotnet build AwsRagChat\AwsRagChat.Application\AwsRagChat.Application.csproj
dotnet build AwsRagChat\AwsRagChat.Infrastructure\AwsRagChat.Infrastructure.csproj
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Run final verification after all Phase 3 changes:

```powershell
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

Expected build impact:

- Application must compile after new provider-neutral contracts.
- Infrastructure must compile after `IStorageProvider` changes.
- Ingestion must compile after `AwsDocumentProcessor` and handler updates.
- API should compile because existing legacy interfaces and controller contracts are preserved.

If API build fails due to locked output DLLs from a running API process:

- Confirm the failure is file-lock related.
- Verify `Application`, `Infrastructure`, and `Ingestion` builds.
- Stop the running API process only with explicit approval.

Recommended code searches during verification:

```powershell
rg -n "GetObjectAsync|IAmazonS3|TextExtractionService|TextractTextExtractionService|TextractAsyncExtractionService" AwsRagChat\AwsRagChat.Ingestion\Handlers
rg -n "Amazon\.|IAmazon|S3Event|SQSEvent|Textract|S3Object" AwsRagChat\AwsRagChat.Application
rg -n "Azure|AzureBlob|AzureOpenAi|Cosmos|Entra|Document Intelligence" AwsRagChat
```

Expected search results:

- Handlers may still reference `S3Event`, `SQSEvent`, and AWS Lambda namespaces.
- Handlers should no longer directly reference `IAmazonS3`, direct S3 reads, or low-level Textract extraction services.
- Application interfaces and models should not reference AWS SDK types.
- No Azure implementation code should exist.

## Expected Multi-Cloud Readiness Improvement

Current readiness after Phase 2: **5.8/10**.

Expected readiness after Phase 3: **6.4/10** to **6.7/10**.

Expected subsystem improvements:

| Subsystem | Phase 2 score | Expected Phase 3 score | Improvement |
|---|---:|---:|---|
| Storage | 6/10 | 7/10 | Ingestion reads move behind storage provider abstraction. |
| OCR | 2/10 | 5/10 | Textract routing moves behind `IDocumentProcessor`. |
| Ingestion | 5/10 | 6.5/10 | Handlers become thinner AWS adapters and stop coordinating extraction details. |
| Events | 2/10 | 2/10 | Event abstraction is not part of Phase 3. |
| Embeddings | 7/10 | 7/10 | No major embedding change expected. |
| Chat | 7/10 | 7/10 | No major chat change expected. |
| Vector Search | 7/10 | 7/10 | No major vector-store change expected. |
| Persistence | 6/10 | 6/10 | DynamoDB persistence remains. |
| Authentication | 3/10 | 3/10 | Identity normalization remains later work. |
| Frontend | 3/10 | 3/10 | Frontend cleanup remains later work. |

Phase 3 should materially improve ingestion portability by creating the future slot for Azure AI Document Intelligence and Azure Blob read support. It will not make the platform cloud-switchable yet because event handling, identity, persistence, external naming, and Azure provider implementations remain future phases.
