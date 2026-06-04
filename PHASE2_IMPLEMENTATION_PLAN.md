# Phase 2 Implementation Plan

## Objective

Implement Phase 2 as a cohesive unit:

- Normalize provider-neutral contracts and AWS registrations.
- Preserve all current AWS behavior.
- Keep backward compatibility with existing interfaces.
- Do not add Azure implementations.
- Do not change Lambda handler signatures.
- Reduce duplicated Lambda composition by introducing an AWS ingestion composition/helper.

Phase 2 is not intended to solve OCR abstraction, storage-read abstraction, `S3Key` naming, frontend auth, or Azure support. Those remain later phases.

## 1. Files To Modify

### Application Layer

- `AwsRagChat.Application/Interfaces/IStorageProvider.cs`
- `AwsRagChat.Application/Interfaces/IEmbeddingProvider.cs`
- `AwsRagChat.Application/Interfaces/IChatProvider.cs`
- `AwsRagChat.Application/Interfaces/IVectorStore.cs`

Expected changes:

- Keep these provider-neutral interfaces.
- Avoid adding Azure-specific concepts.
- Avoid removing old interfaces.

### Infrastructure Layer

- `AwsRagChat.Infrastructure/DependencyInjection.cs`
- `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs`
- `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs`
- `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs`
- `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`
- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`
- `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`

Expected changes:

- Register provider-neutral interfaces for AWS.
- Preserve existing old-interface registrations.
- Make existing AWS implementations satisfy provider-neutral interfaces where needed.
- Keep `CloudProvider = AWS` behavior unchanged.
- Keep unsupported non-AWS behavior unchanged or clearer.

### Ingestion Layer

- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`
- New likely file: `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`
- Optional new likely file: `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`

Expected changes:

- Move duplicated AWS service construction into a helper/composition object.
- Keep Lambda handler signatures unchanged.
- Keep S3/SQS/SNS/Textract/Bedrock/DynamoDB/OpenSearch behavior unchanged.
- Keep the handlers as AWS adapters.

## 2. Exact Changes Required

### 2.1 Provider-Neutral Interface Registration

Current state:

- `IStorageProvider` exists but `S3StorageService` implements only `IStorageService`.
- `IEmbeddingProvider` exists.
- `EmbeddingBatchService` implements `IEmbeddingProvider` for ingestion.
- `BedrockEmbeddingService` implements only `IEmbeddingService`.
- `IChatProvider` exists but `BedrockChatCompletionService` implements only `IChatCompletionService`.
- `IVectorStore` exists and `OpenSearchService` implements it.

Required changes:

1. Update `S3StorageService` to implement `IStorageProvider`.

   Exact class declaration target:

   ```text
   S3StorageService : IStorageProvider
   ```

   Rationale:

   - `IStorageProvider` currently inherits `IStorageService`, so this is backward-compatible.
   - No method changes required.

2. Update `BedrockEmbeddingService` to implement `IEmbeddingProvider`.

   Exact class declaration target:

   ```text
   BedrockEmbeddingService : IEmbeddingProvider
   ```

   Rationale:

   - `IEmbeddingProvider` currently inherits `IEmbeddingService`.
   - No method changes required.

3. Update `BedrockChatCompletionService` to implement `IChatProvider`.

   Exact class declaration target:

   ```text
   BedrockChatCompletionService : IChatProvider
   ```

   Rationale:

   - `IChatProvider` currently inherits `IChatCompletionService`.
   - No method changes required.

4. Keep `OpenSearchService : IVectorStore`.

   No class declaration change needed if already done.

5. Do not remove existing old interfaces.

   Keep:

   - `IStorageService`
   - `IEmbeddingService`
   - `IChatCompletionService`
   - `IVectorSearchService`

### 2.2 Infrastructure DI Changes

File:

- `AwsRagChat.Infrastructure/DependencyInjection.cs`

Required changes:

1. Register AWS implementations for both old and new interfaces.

   Target registrations:

   ```text
   IStorageProvider      -> S3StorageService
   IStorageService       -> same S3StorageService-compatible registration

   IEmbeddingProvider    -> BedrockEmbeddingService
   IEmbeddingService     -> same BedrockEmbeddingService-compatible registration

   IChatProvider         -> BedrockChatCompletionService
   IChatCompletionService -> same BedrockChatCompletionService-compatible registration

   IVectorStore          -> OpenSearchService
   IVectorSearchService  -> same OpenSearchService-compatible registration
   ```

2. Prefer preserving scoped lifetimes unless an existing service requires singleton.

   Current lifetimes are scoped for most application services:

   - `IStorageService`
   - `IChunkRepository`
   - `IDocumentRepository`
   - `IConversationRepository`
   - `IUserRepository`
   - `IEmbeddingService`
   - `IChatCompletionService`
   - `IVectorSearchService`

   Keep scoped lifetimes to avoid behavioral changes.

3. Avoid accidentally creating separate instances when old and new interfaces are resolved in the same scope.

   Preferred registration shape:

   ```text
   AddScoped<S3StorageService>()
   AddScoped<IStorageProvider>(sp => sp.GetRequiredService<S3StorageService>())
   AddScoped<IStorageService>(sp => sp.GetRequiredService<S3StorageService>())
   ```

   Apply the same pattern for:

   - `BedrockEmbeddingService`
   - `BedrockChatCompletionService`
   - `OpenSearchService`

4. Keep repository registrations unchanged.

   Repositories remain:

   - `IChunkRepository -> DynamoDbChunkRepository`
   - `IDocumentRepository -> DynamoDbDocumentRepository`
   - `IConversationRepository -> DynamoDbConversationRepository`
   - `IUserRepository -> DynamoDbUserRepository`

5. Keep `IRedisCacheService -> RedisCacheService` unchanged.

6. Keep `IUserRoleService -> CognitoUserRoleService` unchanged.

### 2.3 AWS Provider Registration Changes

File:

- `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs`

Required changes:

1. Keep AWS SDK registrations unchanged:

   - `IAmazonS3`
   - `IAmazonDynamoDB`
   - `IAmazonBedrockRuntime`
   - `IAmazonCognitoIdentityProvider`

2. Reconsider direct `AddSingleton<OpenSearchService>()`.

   Current state:

   - AWS provider registers `OpenSearchService` singleton.
   - Infrastructure DI also registers vector service scoped.

   Phase 2 target:

   - Avoid lifetime conflicts.
   - Prefer letting `AddInfrastructure(configuration)` own application-service lifetimes.
   - If `OpenSearchService` should remain singleton, register all its interface mappings consistently as singleton.

   Recommended low-risk choice:

   - Remove or avoid the extra direct singleton registration if it is not required.
   - Let `DependencyInjection.AddInfrastructure` register `OpenSearchService` scoped and map interfaces.

   Risk note:

   - Changing singleton to scoped may alter reuse behavior, but `OpenSearchService` is stateless aside from client/index config.
   - If minimizing lifetime change is more important, make all mappings singleton. This must be done consistently.

3. Keep `AddInfrastructure(configuration)` call unchanged.

4. Do not add Azure registration.

### 2.4 Cloud Provider Switch Changes

File:

- `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs`

Required changes:

1. Keep behavior:

   - `CloudProvider` missing or empty defaults to AWS.
   - `CloudProvider = AWS` works.
   - Any other provider throws unsupported provider.

2. Optional safe improvement:

   - Make unsupported-provider message mention that only AWS is implemented in this phase.

3. Do not add Azure branch.

## 3. Dependency Injection Changes

### Target API Runtime DI

The API runtime should support both old and new abstractions at the same time:

```text
S3StorageService
  -> IStorageService
  -> IStorageProvider

BedrockEmbeddingService
  -> IEmbeddingService
  -> IEmbeddingProvider

BedrockChatCompletionService
  -> IChatCompletionService
  -> IChatProvider

OpenSearchService
  -> IVectorSearchService
  -> IVectorStore
```

Existing application services may keep using old interfaces:

- `ChatService` may keep using `IChatCompletionService` and `IStorageService`.
- `RetrievalService` may keep using `IEmbeddingService` and `IVectorSearchService`.

New or refactored code may use provider-neutral interfaces:

- `IStorageProvider`
- `IEmbeddingProvider`
- `IChatProvider`
- `IVectorStore`

### Target Ingestion Composition

Ingestion currently has manual constructor setup in two Lambda handlers. Phase 2 should centralize that construction without changing handler signatures.

Target composition helper creates:

- AWS clients:
  - `AmazonS3Client`
  - `AmazonDynamoDBClient`
  - `AmazonBedrockRuntimeClient`
  - `AmazonTextractClient`
- Options:
  - ingestion `DynamoDbOptions`
  - infrastructure `DynamoDbOptions`
  - ingestion `BedrockOptions`
  - `TextractAsyncOptions`
- Services:
  - `TextExtractionService`
  - `TextractTextExtractionService`
  - `TextractAsyncExtractionService`
  - `ChunkingService`
  - `EmbeddingBatchService`
  - `DynamoDbChunkRepository`
  - `DocumentStatusService`
  - `OpenSearchService`
  - `DocumentIngestionPipeline`

No runtime behavior should change; the same concrete AWS services are still used.

## 4. AWS Registration Changes

### Infrastructure AWS Registrations

Keep AWS as the only supported provider.

Register AWS-backed implementations for provider-neutral interfaces:

| Interface | AWS implementation | Current behavior |
|---|---|---|
| `IStorageProvider` | `S3StorageService` | Same S3 upload/read URL behavior |
| `IEmbeddingProvider` | `BedrockEmbeddingService` | Same Bedrock embedding behavior |
| `IChatProvider` | `BedrockChatCompletionService` | Same Bedrock Nova chat behavior |
| `IVectorStore` | `OpenSearchService` | Same OpenSearch search/index behavior |

Keep backward-compatible registrations:

| Legacy interface | AWS implementation |
|---|---|
| `IStorageService` | `S3StorageService` |
| `IEmbeddingService` | `BedrockEmbeddingService` |
| `IChatCompletionService` | `BedrockChatCompletionService` |
| `IVectorSearchService` | `OpenSearchService` |

### Ingestion AWS Composition

Ingestion is not currently using `IServiceCollection`/DI. Phase 2 should not force full DI into Lambda handlers.

Use a simple helper/factory instead:

```text
AwsIngestionComposition
  Create(configuration)
    -> AwsIngestionServices
```

This keeps the change small and avoids altering Lambda runtime entry points.

## 5. Lambda Composition/Helper Design

### Problem

Both Lambda handlers duplicate service construction:

- `S3DocumentIngestionFunction`
- `TextractCompletionFunction`

They both create:

- DynamoDB options.
- Bedrock options.
- Textract options.
- AWS clients.
- Chunking service.
- Embedding service.
- Chunk repository.
- Document status service.
- OpenSearch service.
- Document ingestion pipeline.

### Proposed Helper

New file:

- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`

Potential shape:

```text
AwsIngestionComposition
  static AwsIngestionServices Create(IConfiguration configuration)
```

New file:

- `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`

Potential shape:

```text
AwsIngestionServices
  IAmazonS3 AmazonS3
  TextExtractionService TextExtractionService
  TextractTextExtractionService TextractTextExtractionService
  TextractAsyncExtractionService TextractAsyncExtractionService
  IDocumentStatusService DocumentStatusService
  IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult> DocumentIngestionPipeline
```

The helper should build exactly the same concrete services as the handlers currently build.

### Handler Changes

`S3DocumentIngestionFunction` constructor becomes composition-only:

```text
configuration = build appsettings
services = AwsIngestionComposition.Create(configuration)
_amazonS3 = services.AmazonS3
_textExtractionService = services.TextExtractionService
_textractTextExtractionService = services.TextractTextExtractionService
_textractAsyncExtractionService = services.TextractAsyncExtractionService
_documentStatusService = services.DocumentStatusService
_documentIngestionPipeline = services.DocumentIngestionPipeline
```

`TextractCompletionFunction` constructor similarly uses composition helper.

### What The Helper Must Not Do

- Do not change event processing.
- Do not change S3 key parsing.
- Do not change Textract request logic.
- Do not change SQS/SNS message parsing.
- Do not introduce Azure code.
- Do not change status messages.
- Do not rename `S3Key`.

### Why Not Full Lambda DI Yet

Full Lambda DI would be larger and may affect cold start/runtime behavior. A composition helper gives most of the architectural benefit:

- One place for AWS ingestion object graph.
- Less duplication.
- Easier future transition to real DI or provider-specific factories.

## 6. Risks

### Medium: Lifetime Mismatch

Registering both old and new interfaces can accidentally create multiple service instances per scope if each interface is registered independently.

Mitigation:

- Register concrete type once.
- Map interfaces to the same concrete instance within the scope.

### Medium: OpenSearch Lifetime Conflict

`OpenSearchService` may currently be registered both singleton and scoped.

Mitigation:

- Choose one lifetime and use it consistently.
- Prefer scoped through `AddInfrastructure` unless there is a known reason for singleton.

### Medium: Lambda Composition Drift

Moving construction into a helper may accidentally change which options class or table name is used.

Mitigation:

- Preserve both ingestion and infrastructure `DynamoDbOptions` exactly as currently used.
- Use explicit namespace aliases.
- Keep `DocumentStatusService` table name source unchanged:
  - `DynamoDb:DocumentsTableName`

### Low-Medium: Interface Declaration Changes

Making AWS classes implement provider-neutral interfaces should be compile-time safe because the provider interfaces inherit existing contracts.

Mitigation:

- Do not alter method signatures.
- Do not remove legacy interfaces.

### Low: Handler Runtime Behavior

The handler signatures and method bodies should remain mostly unchanged.

Mitigation:

- Only move construction logic.
- Keep event-processing methods untouched.

## 7. Build Verification Steps

Run after each commit boundary:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Run after all Phase 2 changes:

```powershell
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

If API build fails due to locked output DLLs from a running API process:

- Confirm the failure is file-lock related.
- Build `AwsRagChat.Application`, `AwsRagChat.Infrastructure`, and `AwsRagChat.Ingestion`.
- Stop the running API process only with explicit user approval if required.

Optional frontend verification:

```powershell
npm test
```

Only needed if Phase 2 unexpectedly touches frontend, which it should not.

Expected build impact:

- Ingestion project should compile after composition helper changes.
- API project should compile after DI registration changes.
- No frontend build impact.

## 8. Estimated Commit Boundaries

### Commit 1: AWS Implementations Adopt Provider-Neutral Interfaces

Files:

- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`
- `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs`
- `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`
- `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`

Changes:

- Make AWS classes implement target provider-neutral interfaces.
- No method changes.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

Risk:

- Low

### Commit 2: Register Provider-Neutral Interfaces For AWS

Files:

- `AwsRagChat.Infrastructure/DependencyInjection.cs`
- `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs`

Changes:

- Register concrete AWS implementations once.
- Map both legacy and provider-neutral interfaces to the same implementation.
- Resolve `OpenSearchService` lifetime consistency.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

Risk:

- Medium

### Commit 3: Add AWS Ingestion Composition Helper

Files:

- `AwsRagChat.Ingestion/Aws/AwsIngestionComposition.cs`
- `AwsRagChat.Ingestion/Aws/AwsIngestionServices.cs`

Changes:

- Create a helper that builds the existing AWS ingestion object graph.
- Preserve options/classes exactly.
- Do not update handlers yet.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Low-medium

### Commit 4: Use Composition Helper In S3 Lambda Handler

Files:

- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`

Changes:

- Replace constructor object graph duplication with `AwsIngestionComposition.Create(configuration)`.
- Keep `FunctionHandler` unchanged.
- Keep S3 event handling unchanged.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Medium

### Commit 5: Use Composition Helper In Textract Completion Handler

Files:

- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`

Changes:

- Replace constructor object graph duplication with `AwsIngestionComposition.Create(configuration)`.
- Keep `FunctionHandler` unchanged.
- Keep SQS/SNS parsing unchanged.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
```

Risk:

- Medium

### Commit 6: Final Phase 2 Verification

Files:

- No intended code changes.

Actions:

- Build ingestion.
- Build API.
- Review DI registrations.
- Confirm no Azure code was added.
- Confirm handler signatures unchanged.

Build:

```powershell
dotnet build AwsRagChat\AwsRagChat.Ingestion\AwsRagChat.Ingestion.csproj
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
```

Risk:

- Low

## Phase 2 Completion Criteria

Phase 2 is complete when:

- AWS implementations are registered under provider-neutral interfaces.
- Legacy interfaces still resolve.
- AWS provider registration still supports current runtime behavior.
- Ingestion handlers use a shared AWS composition helper.
- Lambda handler signatures are unchanged.
- S3/SQS/SNS/Textract/Bedrock/DynamoDB/OpenSearch behavior is unchanged.
- No Azure implementation exists.
- Ingestion build succeeds.
- API build succeeds or only fails due to an externally running API file lock.
