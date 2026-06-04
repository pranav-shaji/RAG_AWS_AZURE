# Multi-Cloud Backlog and Roadmap

## 1. Current Architecture State

The repository is now partially prepared for provider-neutral ingestion and RAG runtime switching.

Current state after Phase 1 Step 4:

- API business flow remains AWS-backed but mostly application-interface driven.
- Application layer now contains provider-neutral interface names:
  - `IStorageProvider`
  - `IEmbeddingProvider`
  - `IChatProvider`
  - `IVectorStore`
  - `IDocumentStatusService`
  - `IIngestionPipeline<TRequest, TExtractedDocument, TResult>`
- Existing compatibility interfaces remain:
  - `IStorageService`
  - `IEmbeddingService`
  - `IChatCompletionService`
  - `IVectorSearchService`
- `DocumentIngestionPipeline` now depends on:
  - `IEmbeddingProvider`
  - `IChunkRepository`
  - `IDocumentStatusService`
  - `IVectorStore`
  - concrete `ChunkingService`
- `EmbeddingBatchService` implements `IEmbeddingProvider`.
- `DocumentStatusService` implements `IDocumentStatusService`.
- `OpenSearchService` implements `IVectorStore`.
- Ingestion chunk persistence now goes through `IChunkRepository` using `DynamoDbChunkRepository`.
- AWS Lambda handlers still manually create AWS clients and concrete AWS services.
- No Azure implementations exist.
- No business logic has been intentionally changed.

## 2. Remaining AWS-Specific Dependencies

### Storage

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| S3 upload/read URL | `AwsRagChat.Infrastructure/Storage/S3StorageService.cs` | Partially abstracted | API uses `IStorageService`; target `IStorageProvider` exists but storage read/open-stream is not yet defined. |
| S3 object read during ingestion | `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | Not abstracted | Handler creates `AmazonS3Client` and calls `GetObjectAsync` directly. |
| S3 event trigger | `S3DocumentIngestionFunction.FunctionHandler(S3Event, ILambdaContext)` | Not abstracted | AWS event type is correctly isolated to handler but no neutral adapter model exists yet. |
| `S3Key` domain/API/frontend terminology | `DocumentChunk.cs`, `IDocumentRepository.cs`, `UploadDocumentResponse.cs`, `api.ts`, repositories, ingestion status | Not abstracted | Storage vocabulary leaks through domain, DTOs, persistence, frontend. |

### OCR

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Textract sync OCR | `TextractTextExtractionService.cs` | Not abstracted | Direct `IAmazonTextract` and Textract request/response models. |
| Textract async OCR | `TextractAsyncExtractionService.cs` | Not abstracted | Direct Textract async APIs, SNS role/topic options, job IDs. |
| OCR routing | `S3DocumentIngestionFunction.cs`, `TextractCompletionFunction.cs` | Not abstracted | Handler logic chooses direct extraction vs Textract. |
| Direct extraction | `TextExtractionService.cs` | Already cloud-neutral | PDF/text/CSV extraction is local/provider-neutral. |

### Embeddings

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Bedrock API call in API runtime | `BedrockEmbeddingService.cs` | Partially abstracted | Implements `IEmbeddingService`; should also be registered through `IEmbeddingProvider` in a provider module later. |
| Bedrock API call in ingestion | `EmbeddingBatchService.cs` | Partially abstracted | Now implements `IEmbeddingProvider`, but still directly uses `IAmazonBedrockRuntime`. |
| Bedrock config naming | `BedrockOptions.cs`, `appsettings.json` | Not abstracted | Config section is still `Bedrock`. |

### Chat

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Bedrock chat generation | `BedrockChatCompletionService.cs` | Partially abstracted | Implements `IChatCompletionService`; target `IChatProvider` exists but DI still registers old service interface. |
| Nova request/response shape | `BedrockChatCompletionService.InvokeNovaAsync` | Already isolated | Provider-specific payload parsing is inside infrastructure. |
| Bedrock/Nova naming | logs/config/options/frontend text | Not abstracted | Naming remains AWS-specific. |

### Vector Search

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| OpenSearch vector search | `OpenSearchService.cs` | Partially abstracted | Implements `IVectorStore`; still AWS SigV4/OpenSearch-specific implementation. |
| OpenSearch indexing in ingestion | `DocumentIngestionPipeline.cs` via `IVectorStore` | Already abstracted at pipeline level | Concrete implementation is still AWS, but pipeline dependency is now neutral. |
| OpenSearch logs/config names | `RetrievalService.cs`, `DocumentIngestionPipeline.cs`, appsettings | Not abstracted | Logs and config still refer to OpenSearch. |

### Persistence

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| DynamoDB repositories | `DynamoDbChunkRepository.cs`, `DynamoDbDocumentRepository.cs`, `DynamoDbConversationRepository.cs`, `DynamoDbUserRepository.cs` | Partially abstracted | Application uses repository interfaces; implementations are AWS-specific. |
| Document status persistence | `DocumentStatusService.cs` | Partially abstracted | Implements `IDocumentStatusService`; still direct DynamoDB implementation. |
| Duplicate `DynamoDbOptions` | `AwsRagChat.Ingestion/Options/DynamoDbOptions.cs`, `AwsRagChat.Infrastructure/Options/DynamoDbOptions.cs` | Not abstracted | Identical config shape exists in two namespaces. |
| DynamoDB logs/fallback names | `RetrievalService.cs`, ingestion logs | Not abstracted | Application logs mention DynamoDB directly. |

### Authentication

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Cognito JWT validation | `AwsAuthenticationExtensions.cs` | Partially abstracted | Provider switch exists, but only AWS/Cognito is implemented. |
| Cognito role/group management | `CognitoUserRoleService.cs` | Partially abstracted | Uses `IUserRoleService`; implementation is Cognito-specific. |
| Cognito claims | `ClaimsPrincipalExtensions.cs`, `auth.ts` | Not abstracted | Hardcoded `cognito:groups`. |
| Cognito frontend OAuth | `aws-rag-chat-ui/src/app/auth.ts` | Not abstracted | Hosted UI URLs and token flow are Cognito-named. |

### Events

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| S3 object-created event | `S3DocumentIngestionFunction.cs` | Not abstracted | AWS Lambda S3 event is handler boundary only. |
| SQS Textract completion event | `TextractCompletionFunction.cs` | Not abstracted | AWS Lambda SQS event is handler boundary only. |
| SNS envelope parsing | `TextractCompletionFunction.TryGetTextractMessageJson` | Not abstracted | AWS SNS message format parsing is local to handler. |
| Event publishing | none | Not abstracted | `IEventPublisher` has not been implemented. Upload currently relies on S3 events. |

### Ingestion

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Core ingestion pipeline dependencies | `DocumentIngestionPipeline.cs` | Partially abstracted | Pipeline uses interfaces for embeddings, chunk persistence, document status, and vector indexing. |
| Chunking | `ChunkingService.cs` | Already cloud-neutral | Concrete but provider-neutral; no AWS dependency. |
| Lambda handlers | `S3DocumentIngestionFunction.cs`, `TextractCompletionFunction.cs` | Not abstracted | Constructors manually create AWS clients and concrete services. |
| Document processor abstraction | missing | Not abstracted | No `IDocumentProcessor` exists yet. |
| Storage provider read abstraction | missing | Not abstracted | Existing storage abstraction lacks ingestion read stream support. |

### Frontend

| Dependency | Files | Classification | Notes |
|---|---|---|---|
| Cognito auth settings | `environment.ts`, `environment.prod.ts` | Not abstracted | Environment uses `cognitoClientId`, `cognitoDomain`, `cognitoScopes`. |
| Cognito OAuth implementation | `auth.ts` | Not abstracted | Login/logout/token exchange are Cognito-named. |
| Cognito group claim | `auth.ts` | Not abstracted | Uses `cognito:groups`. |
| AWS display text | `app.html`, `chat-page.html` | Not abstracted | Mentions Cognito, Amazon Bedrock, OpenSearch, Titan embeddings. |
| API DTO `s3Key` | `api.ts` | Not abstracted | Frontend contract still exposes AWS storage vocabulary. |

## 3. Remaining Work Estimate

| Area | Remaining work | Estimate |
|---|---|---:|
| Storage abstraction cleanup | Add storage read/open support, rename storage vocabulary, keep compatibility with `S3Key`. | 4-7 days |
| OCR/document processing | Introduce `IDocumentProcessor`; wrap Textract/direct extraction logic. | 5-8 days |
| Ingestion adapters | Keep Lambda handlers as AWS adapters, move orchestration construction behind factories/DI. | 5-8 days |
| Provider DI registration | Register provider-neutral interfaces consistently for AWS. | 2-4 days |
| Identity normalization | Configurable claims, provider-neutral auth frontend/backend model. | 5-8 days |
| Event abstraction | Introduce `IEventPublisher`; define neutral events; keep AWS S3/SQS behavior. | 4-6 days |
| Naming leakage | Migrate `S3Key`, `Bedrock`, `OpenSearch`, `DynamoDB` terminology where exposed to domain/DTO/frontend. | 4-7 days |
| Azure implementation readiness | Prepare extension points without Azure code. | 2-3 days |

## 4. Roadmap

## Phase 2

Objective:

Normalize provider-neutral contracts and AWS registrations while preserving current AWS behavior.

Files likely affected:

- `AwsRagChat.Infrastructure/DependencyInjection.cs`
- `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs`
- `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs`
- `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs`
- `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`
- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`
- `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`
- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`

Risk level:

- Medium

Estimated effort:

- 1-2 weeks

Key outcomes:

- Register `IStorageProvider`, `IEmbeddingProvider`, `IChatProvider`, and `IVectorStore` for AWS.
- Keep old service interfaces registered for backward compatibility.
- Add a small AWS ingestion service factory or composition helper to reduce manual constructor duplication in both Lambda handlers.
- Do not change Lambda handler signatures.

## Phase 3

Objective:

Introduce document processing and storage-read abstractions so ingestion can stop directly coordinating S3 stream reads and Textract services.

Files likely affected:

- `AwsRagChat.Application/Interfaces/IDocumentProcessor.cs`
- `AwsRagChat.Application/Interfaces/IStorageProvider.cs`
- `AwsRagChat.Ingestion/Services/TextExtractionService.cs`
- `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs`
- `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs`
- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`
- `AwsRagChat.Infrastructure/Storage/S3StorageService.cs`

Risk level:

- Medium-high

Estimated effort:

- 1-2 weeks

Key outcomes:

- Add `IDocumentProcessor`.
- Wrap existing direct extraction + Textract sync/async behavior in AWS document processor.
- Add storage object read/open capability to `IStorageProvider`.
- Keep S3/SQS/SNS event handlers as AWS adapters.
- Preserve Textract behavior and current fallback rules.

## Phase 4

Objective:

Remove AWS leakage from externally visible contracts and identity handling.

Files likely affected:

- `AwsRagChat.Domain/Entities/DocumentChunk.cs`
- `AwsRagChat.Application/Interfaces/IDocumentRepository.cs`
- `AwsRagChat.Application/DTOs/UploadDocumentResponse.cs`
- `AwsRagChat.Application/Services/ChatService.cs`
- `AwsRagChat.Application/Services/RetrievalService.cs`
- `AwsRagChat.Application/Services/PromptBuilder.cs`
- `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs`
- `AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs`
- `AwsRagChat.Ingestion/Services/DocumentStatusService.cs`
- `AwsRagChat.Api/Security/ClaimsPrincipalExtensions.cs`
- `AwsRagChat.Infrastructure/Aws/AwsAuthenticationExtensions.cs`
- `aws-rag-chat-ui/src/app/api.ts`
- `aws-rag-chat-ui/src/app/auth.ts`
- `aws-rag-chat-ui/src/environments/environment.ts`
- `aws-rag-chat-ui/src/environments/environment.prod.ts`

Risk level:

- High

Estimated effort:

- 2-3 weeks

Key outcomes:

- Introduce `StorageKey` or `ObjectKey` while maintaining compatibility with existing `S3Key`.
- Add configurable role/group claim names.
- Make frontend auth config provider-neutral.
- Remove Cognito-specific display text.
- Keep data migration/backward compatibility for existing DynamoDB attributes.

## Phase 5

Objective:

Prepare provider switching for Azure implementations without adding Azure code yet.

Files likely affected:

- `AwsRagChat.Infrastructure/Common/CloudProviderNames.cs`
- `AwsRagChat.Infrastructure/Common/CloudProviderOptions.cs`
- `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs`
- `AwsRagChat.Api/appsettings.json`
- `AwsRagChat.Ingestion/appsettings.json`
- deployment docs/config files

Risk level:

- Medium

Estimated effort:

- 1 week

Key outcomes:

- Add recognized provider names and clear unsupported-provider errors.
- Define configuration sections for `Storage`, `Embedding`, `Chat`, `VectorStore`, `Identity`, `Events`, and `DocumentProcessing`.
- Keep AWS config backward-compatible.
- Establish extension points for future Azure provider module.
- No Azure implementation yet.

## 5. Updated Multi-Cloud Readiness Score

| Subsystem | Score | Notes |
|---|---:|---|
| Storage | 6/10 | API storage is abstracted; ingestion S3 read/events and `S3Key` leakage remain. |
| OCR | 2/10 | Textract remains direct and unabstracted. |
| Embeddings | 7/10 | Pipeline and retrieval can use embedding interfaces; AWS implementation remains Bedrock. |
| Chat | 7/10 | Chat generation is isolated behind interfaces, but naming/config remain Bedrock-specific. |
| Vector Search | 7/10 | Pipeline and retrieval have vector interfaces; OpenSearch implementation remains AWS-specific. |
| Persistence | 6/10 | Repositories are abstracted; DynamoDB implementations and option duplication remain. |
| Authentication | 3/10 | Cognito is still hardcoded in backend/frontend claims and OAuth flow. |
| Events | 2/10 | S3/SQS/SNS are not abstracted; no `IEventPublisher`. |
| Ingestion | 4/10 | Core pipeline dependencies improved; handlers still create AWS clients and coordinate AWS services. |
| Frontend | 3/10 | API calls are neutral, but auth, text, and `s3Key` are AWS-specific. |

Overall readiness: **5.3/10**.

## 6. Next Highest-Impact Refactor

Recommended next single highest-impact refactor:

**Introduce `IDocumentProcessor` and wrap the existing direct extraction + Textract logic behind an AWS document processor, without changing Lambda handler signatures.**

Why this is highest impact:

- OCR is currently the least abstracted core RAG dependency after event triggers.
- It removes Textract coordination from handlers.
- It creates the future slot for Azure AI Document Intelligence.
- It keeps current S3/SQS/SNS handler boundaries intact.
- It directly advances the target architecture without touching frontend/auth or risky `S3Key` renaming.

Recommended boundary:

- Add `IDocumentProcessor` in Application interfaces.
- Create an AWS-backed implementation using existing:
  - `TextExtractionService`
  - `TextractTextExtractionService`
  - `TextractAsyncExtractionService`
- Update handlers to delegate extraction decisions to this abstraction.
- Preserve all Textract behavior and messages.
- Do not introduce Azure code.
