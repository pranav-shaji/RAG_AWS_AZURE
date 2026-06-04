# Phase 2 Completion Report

## Summary

Phase 2 is complete.

The repository now has AWS implementations registered under provider-neutral contracts while preserving the legacy application interfaces. The ingestion Lambda handlers still expose their AWS Lambda event signatures, but their duplicated constructor object graph creation has been moved into a shared AWS ingestion composition helper.

No Azure implementation was added, and no Phase 3 document-processing or storage-read abstraction work was implemented.

## Verification

Build verification was completed on June 4, 2026.

| Project | Command | Result |
|---|---|---|
| Ingestion | `dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj` | Succeeded, 0 warnings, 0 errors |
| Infrastructure | `dotnet build AwsRagChat\AwsRagChat.Infrastructure\AwsRagChat.Infrastructure.csproj` | Succeeded, 0 warnings, 0 errors |
| API | `dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj` | Succeeded, 0 warnings, 0 errors |

## What Was Completed

### Provider-Neutral Interface Adoption

AWS runtime implementations now satisfy the provider-neutral interfaces:

| Provider-neutral interface | AWS implementation |
|---|---|
| `IStorageProvider` | `S3StorageService` |
| `IEmbeddingProvider` | `BedrockEmbeddingService` |
| `IChatProvider` | `BedrockChatCompletionService` |
| `IVectorStore` | `OpenSearchService` |

The ingestion embedding service also satisfies the provider-neutral embedding contract:

| Provider-neutral interface | Ingestion implementation |
|---|---|
| `IEmbeddingProvider` | `EmbeddingBatchService` |

### Backward-Compatible Registrations

The legacy interfaces remain registered and continue to resolve:

| Legacy interface | AWS implementation |
|---|---|
| `IStorageService` | `S3StorageService` |
| `IEmbeddingService` | `BedrockEmbeddingService` |
| `IChatCompletionService` | `BedrockChatCompletionService` |
| `IVectorSearchService` | `OpenSearchService` |

The concrete AWS implementation is registered once per scope and both old and new interfaces map to the same scoped instance.

### AWS Provider Registration

`CloudProvider = AWS` remains the only supported runtime provider.

The provider switch still defaults missing or empty `CloudProvider` to AWS. Unsupported providers throw a clear error stating that only AWS is available in this build.

AWS SDK service registrations remain in the AWS provider module:

- `IAmazonS3`
- `IAmazonDynamoDB`
- `IAmazonBedrockRuntime`
- `IAmazonCognitoIdentityProvider`

### Ingestion Composition

The duplicated constructor object graph in both Lambda handlers has been replaced with:

```text
AwsIngestionComposition.Create(configuration)
```

The shared composition helper now constructs the AWS ingestion object graph:

- `AmazonS3Client`
- `AmazonDynamoDBClient`
- `AmazonBedrockRuntimeClient`
- `AmazonTextractClient`
- ingestion and infrastructure options
- `TextExtractionService`
- `TextractTextExtractionService`
- `TextractAsyncExtractionService`
- `ChunkingService`
- `EmbeddingBatchService`
- `DynamoDbChunkRepository`
- `DocumentStatusService`
- `OpenSearchService`
- `DocumentIngestionPipeline`

The Lambda handler signatures were preserved:

```text
S3DocumentIngestionFunction.FunctionHandler(S3Event, ILambdaContext)
TextractCompletionFunction.FunctionHandler(SQSEvent, ILambdaContext)
```

The S3 event flow, SQS/SNS parsing, Textract behavior, Bedrock behavior, DynamoDB behavior, OpenSearch behavior, and document status updates remain AWS-backed and unchanged in intent.

## Current Architecture State

The API and application runtime are partially provider-neutral:

- Controllers and application services continue to depend mostly on application interfaces.
- AWS infrastructure implementations are selected through the existing provider switch.
- New provider-neutral contracts are available for future code.
- Legacy contracts remain available for existing code.
- No Azure provider module exists.

The ingestion runtime is improved but still AWS-adapter based:

- Lambda event handlers remain AWS-specific adapters.
- Handler-level service construction is centralized.
- Core ingestion pipeline dependencies use interfaces for embedding, chunk persistence, document status, and vector indexing.
- S3 stream reading and Textract orchestration remain in handler/service code pending Phase 3.

## Remaining AWS-Specific Areas

### Storage

- `S3StorageService` remains the AWS storage implementation.
- `S3DocumentIngestionFunction` still receives `S3Event`.
- `S3DocumentIngestionFunction` still reads document content through `IAmazonS3.GetObjectAsync`.
- Domain/API/frontend contracts still expose `S3Key`.
- S3 configuration sections remain named `S3`.

### OCR and Document Processing

- `TextractTextExtractionService` and `TextractAsyncExtractionService` directly use Amazon Textract SDK models.
- Textract async completion still depends on SQS/SNS message shape and S3 document locations.
- There is still no `IDocumentProcessor` abstraction.
- Direct extraction is provider-neutral, but OCR routing is still AWS/Textract-specific.

### Embeddings and Chat

- `BedrockEmbeddingService` and `BedrockChatCompletionService` remain AWS Bedrock implementations.
- `EmbeddingBatchService` still directly uses `IAmazonBedrockRuntime` for ingestion.
- Bedrock config and log terminology remain AWS-specific.

### Vector Search

- `OpenSearchService` remains the AWS/OpenSearch vector implementation.
- OpenSearch config, logs, and ranking terminology remain in application and infrastructure code.
- OpenSearch indexing/search implementation uses AWS SigV4/OpenSearch-specific client setup.

### Persistence

- DynamoDB repositories remain the active persistence implementations.
- `DocumentStatusService` still updates DynamoDB directly.
- `ChunkPersistenceService` still exists with direct DynamoDB usage, although the current pipeline uses `IChunkRepository`.
- DynamoDB config and logs remain AWS-specific.

### Identity

- Cognito authentication remains the only implemented authentication provider.
- Backend claims still use `cognito:groups`.
- `AuthController` and `CognitoUserRoleService` still depend on Cognito APIs.
- Frontend auth configuration and UI text remain Cognito-specific.

### Events and Handler Coupling

- `S3DocumentIngestionFunction` remains coupled to AWS Lambda, S3 events, and S3 object reads.
- `TextractCompletionFunction` remains coupled to AWS Lambda SQS events and SNS/Textract message parsing.
- Direct AWS client creation no longer exists in the handlers, but remains intentionally centralized in `AwsIngestionComposition`.
- No provider-neutral event publisher exists yet.

## Remaining Direct AWS Client Creation

Direct AWS client construction is now centralized in:

- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`

The centralized constructions are:

```text
new AmazonS3Client()
new AmazonDynamoDBClient()
new AmazonBedrockRuntimeClient()
new AmazonTextractClient()
```

No direct `new Amazon*Client()` construction remains in the two Lambda handler constructors.

## Remaining Handler-Level AWS Coupling

The handlers remain AWS adapters by design:

- `S3DocumentIngestionFunction` imports AWS Lambda S3 event types.
- `S3DocumentIngestionFunction` receives `S3Event`.
- `S3DocumentIngestionFunction` keeps `IAmazonS3` to read the uploaded object stream.
- `TextractCompletionFunction` imports AWS Lambda SQS event types.
- `TextractCompletionFunction` receives `SQSEvent`.
- `TextractCompletionFunction` parses SNS envelopes and Textract completion payloads.
- `TextractCompletionFunction` reads `DocumentLocation.S3Bucket` and `DocumentLocation.S3ObjectName`.

This is expected for Phase 2. Phase 2 centralized composition; it did not introduce provider-neutral event, storage-read, or document-processing abstractions.

## Updated Multi-Cloud Readiness Score

Overall readiness: **5.8/10**.

| Subsystem | Score | Current assessment |
|---|---:|---|
| Storage | 6/10 | API storage has provider-neutral registration, but ingestion S3 reads and `S3Key` leakage remain. |
| OCR | 2/10 | Textract remains direct and unabstracted. |
| Embeddings | 7/10 | Provider-neutral contracts exist for API and ingestion, but implementations remain Bedrock-specific. |
| Chat | 7/10 | Chat provider contract exists; Bedrock implementation and naming remain. |
| Vector Search | 7/10 | `IVectorStore` is registered; OpenSearch implementation and terminology remain. |
| Persistence | 6/10 | Repository boundaries exist; DynamoDB implementations and status persistence remain AWS-specific. |
| Authentication | 3/10 | Cognito auth, claims, role management, and frontend flow remain AWS-specific. |
| Events | 2/10 | No event abstraction yet; S3/SQS/SNS are still handler-level AWS concerns. |
| Ingestion | 5/10 | Pipeline dependencies and composition improved; handlers still coordinate S3/Textract adapter behavior. |
| Frontend | 3/10 | Auth, display text, environment names, and `s3Key` remain AWS-specific. |

The score improved from the Phase 1/early Phase 2 state because provider-neutral registrations are now in place and duplicated Lambda composition is centralized. The largest remaining blockers are still OCR/document processing, storage-read abstraction, identity normalization, event abstraction, and external contract naming.

## Recommended Next Phase

Proceed to Phase 3.

Recommended Phase 3 objective:

Introduce document processing and storage-read abstractions so ingestion stops directly coordinating S3 stream reads and Textract services.

Recommended first Phase 3 step:

Add `IDocumentProcessor` in the application layer and create an AWS-backed document processor that wraps the existing direct extraction, Textract sync OCR, and Textract async OCR behavior without changing Lambda handler signatures.

Phase 3 should preserve current AWS behavior and should not add Azure implementation code yet.
