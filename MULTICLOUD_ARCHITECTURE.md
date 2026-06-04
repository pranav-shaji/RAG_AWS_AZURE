# Multi-Cloud Target Architecture

## Goal

The target platform must run on either AWS or Azure by changing configuration and dependency injection only.

```json
{
  "CloudProvider": "AWS"
}
```

or:

```json
{
  "CloudProvider": "Azure"
}
```

Business logic must not know whether the active provider is AWS or Azure. Controllers, application services, RAG orchestration, prompt construction, ranking, citations, admin workflows, and conversation management should depend on provider-neutral interfaces.

## Architecture Principles

1. Application and domain projects stay cloud-neutral.
2. Cloud-specific SDKs live only in provider infrastructure modules.
3. Provider selection happens at startup through DI.
4. Upload, ingestion, retrieval, chat, identity, and events use neutral contracts.
5. Cloud event handlers are thin adapters; ingestion business logic is shared.
6. Storage terminology changes from `S3Key` to `StorageKey` or `ObjectKey`.
7. Vector search terminology changes from `OpenSearch` to `VectorStore`.
8. LLM terminology changes from `Bedrock` to `ChatProvider` and `EmbeddingProvider`.

## Target Layering

```text
Angular Frontend
  |
  v
ASP.NET Core API
  |
  v
Application Layer
  |-- ChatService
  |-- RetrievalService
  |-- ConversationService
  |-- DocumentIngestionService
  |-- PromptBuilder
  |-- QueryIntentClassifier
  |
  v
Provider-Neutral Interfaces
  |-- IStorageProvider
  |-- IDocumentProcessor
  |-- IEmbeddingProvider
  |-- IChatProvider
  |-- IVectorStore
  |-- IIdentityProvider
  |-- IEventPublisher
  |-- repositories
  |-- cache
  |
  +-----------------------------+
  |                             |
  v                             v
AWS Provider Module             Azure Provider Module
  |                             |
  |-- S3                        |-- Blob Storage
  |-- Textract                  |-- Azure AI Document Intelligence
  |-- Bedrock Embeddings        |-- Azure OpenAI Embeddings
  |-- Bedrock Chat              |-- Azure OpenAI Chat
  |-- OpenSearch/AOSS           |-- Azure AI Search
  |-- DynamoDB                  |-- Cosmos DB
  |-- Cognito                   |-- Entra ID / External ID
  |-- SNS/SQS/EventBridge       |-- Event Grid / Service Bus / Queue Storage
  |-- Lambda adapters           |-- Azure Functions adapters
```

## Dependency Injection Model

### Provider Switch

```text
Program.cs
  -> AddCloudProviderAuthentication(configuration)
  -> AddCloudProviderInfrastructure(configuration)

CloudProviderServiceCollectionExtensions
  -> if CloudProvider == "AWS"
       AddAwsAuthentication()
       AddAwsInfrastructure()

  -> if CloudProvider == "Azure"
       AddAzureAuthentication()
       AddAzureInfrastructure()
```

### Expected Registration Shape

```text
CloudProvider = AWS
  IStorageProvider     -> AwsS3StorageProvider
  IDocumentProcessor   -> AwsTextractDocumentProcessor
  IEmbeddingProvider   -> AwsBedrockEmbeddingProvider
  IChatProvider        -> AwsBedrockChatProvider
  IVectorStore         -> AwsOpenSearchVectorStore
  IIdentityProvider    -> AwsCognitoIdentityProvider
  IEventPublisher      -> AwsEventPublisher
  repositories         -> DynamoDB repositories
  cache                -> Redis cache

CloudProvider = Azure
  IStorageProvider     -> AzureBlobStorageProvider
  IDocumentProcessor   -> AzureDocumentIntelligenceProcessor
  IEmbeddingProvider   -> AzureOpenAiEmbeddingProvider
  IChatProvider        -> AzureOpenAiChatProvider
  IVectorStore         -> AzureAiSearchVectorStore
  IIdentityProvider    -> AzureEntraIdentityProvider
  IEventPublisher      -> AzureEventPublisher
  repositories         -> Cosmos DB repositories
  cache                -> Azure Cache for Redis or Redis-compatible cache
```

No controller or application service should branch on `CloudProvider`.

## Target Interfaces

### `IStorageProvider`

Purpose: Store uploaded files, read file content for ingestion, and create temporary read URLs.

Current AWS implementation:

- Existing implementation basis: `S3StorageService`
- Target implementation name: `AwsS3StorageProvider`
- Cloud service: Amazon S3
- Responsibilities:
  - Upload document streams.
  - Return provider-neutral `StorageObjectRef`.
  - Open object stream for ingestion.
  - Create temporary read URLs.

Future Azure implementation:

- Target implementation name: `AzureBlobStorageProvider`
- Cloud service: Azure Blob Storage
- Responsibilities:
  - Upload document streams to a blob container.
  - Return the same `StorageObjectRef` shape.
  - Open blob stream for ingestion.
  - Create temporary SAS read URLs.

Provider-neutral concepts:

```text
StorageObjectRef
  Provider
  ContainerName
  ObjectKey
  Uri
  ContentType
  Metadata
```

### `IDocumentProcessor`

Purpose: Extract text/pages from uploaded documents, including OCR when needed.

Current AWS implementation:

- Existing implementation basis:
  - `TextExtractionService`
  - `TextractTextExtractionService`
  - `TextractAsyncExtractionService`
- Target implementation name: `AwsDocumentProcessor`
- Cloud services:
  - Amazon Textract
  - Amazon S3 object references for Textract input
  - SNS/SQS for async Textract completion
- Responsibilities:
  - Directly extract text/CSV/PDF when possible.
  - Use Textract sync OCR for supported images.
  - Start Textract async jobs for scanned PDFs.
  - Resolve completed OCR output.

Future Azure implementation:

- Target implementation name: `AzureDocumentProcessor`
- Cloud service: Azure AI Document Intelligence
- Responsibilities:
  - Directly extract text/CSV/PDF when possible.
  - Use Document Intelligence for OCR/layout extraction.
  - Normalize Azure analyze results into the same extracted document model.

Provider-neutral output:

```text
ExtractedDocument
  FullText
  PageCount
  Pages[]
    PageNumber
    Text
```

### `IEmbeddingProvider`

Purpose: Generate normalized vector embeddings for questions and document chunks.

Current AWS implementation:

- Existing implementation basis:
  - `BedrockEmbeddingService`
  - `EmbeddingBatchService`
- Target implementation name: `AwsBedrockEmbeddingProvider`
- Cloud service: Amazon Bedrock Runtime
- Model example: `amazon.titan-embed-text-v2:0`

Future Azure implementation:

- Target implementation name: `AzureOpenAiEmbeddingProvider`
- Cloud service: Azure OpenAI
- Model example: configured Azure embedding deployment.

Provider-neutral contract behavior:

- Input: text.
- Output: normalized `IReadOnlyList<float>`.
- Application code must not know provider payload shape or model response JSON.

### `IChatProvider`

Purpose: Invoke LLM chat/generation for grounded answers, general answers, and knowledge overviews.

Current AWS implementation:

- Existing implementation basis: `BedrockChatCompletionService`
- Target implementation name: `AwsBedrockChatProvider`
- Cloud service: Amazon Bedrock Runtime
- Model example: `amazon.nova-lite-v1:0`

Future Azure implementation:

- Target implementation name: `AzureOpenAiChatProvider`
- Cloud service: Azure OpenAI
- Model example: configured Azure chat deployment.

Provider-neutral contract behavior:

- Input: prompt, response mode, inference settings, optional conversation context.
- Output: generated text.
- Prompt construction remains in application layer.
- Provider-specific message envelope and response parsing remain in infrastructure.

### `IVectorStore`

Purpose: Index embedded chunks and run vector search with document, owner, role, and global/shared filters.

Current AWS implementation:

- Existing implementation basis: `OpenSearchService`
- Target implementation name: `AwsOpenSearchVectorStore`
- Cloud service: Amazon OpenSearch Service or OpenSearch Serverless

Future Azure implementation:

- Target implementation name: `AzureAiSearchVectorStore`
- Cloud service: Azure AI Search

Provider-neutral operations:

```text
IndexAsync(DocumentChunk chunk)
SearchAsync(VectorSearchRequest request)
```

Provider-neutral search request:

```text
VectorSearchRequest
  OwnerUserId
  CurrentUserRole
  DocumentId
  SharedDocumentIds
  QueryEmbedding
  TopK
  SearchSharedDocuments
```

Application code should refer to `VectorHits`, `SemanticRankBoost`, and `VectorSearch`, not `OpenSearchHits`.

### `IIdentityProvider`

Purpose: Normalize authentication, user identity, roles/groups, and provider-side role assignment.

Current AWS implementation:

- Existing implementation basis:
  - `AwsAuthenticationExtensions`
  - `CognitoUserRoleService`
  - `ClaimsPrincipalExtensions`
  - Angular `auth.ts`
- Target implementation name: `AwsCognitoIdentityProvider`
- Cloud service: Amazon Cognito
- Claim source: `cognito:groups`

Future Azure implementation:

- Target implementation name: `AzureEntraIdentityProvider`
- Cloud service: Microsoft Entra ID or Entra External ID
- Claim source: configured groups/roles claim.

Provider-neutral behavior:

- Validate bearer tokens.
- Resolve user ID, email, role, groups, and approval scope.
- Assign roles/groups when supported.
- Expose configured frontend OAuth/OIDC settings.

Provider-neutral identity model:

```text
AuthenticatedUser
  UserId
  Email
  DisplayName
  Roles[]
  Groups[]
  Provider
```

### `IEventPublisher`

Purpose: Publish provider-neutral domain events for ingestion, OCR completion, indexing, notifications, and future async workflows.

Current AWS implementation:

- Existing implementation basis:
  - Textract SNS topic settings
  - SQS completion handler
  - S3 event trigger
- Target implementation name: `AwsEventPublisher`
- Cloud services:
  - SNS
  - SQS
  - EventBridge where needed

Future Azure implementation:

- Target implementation name: `AzureEventPublisher`
- Cloud services:
  - Event Grid
  - Service Bus
  - Queue Storage where appropriate

Provider-neutral event examples:

```text
DocumentUploaded
DocumentProcessingStarted
OcrJobStarted
OcrJobCompleted
DocumentIndexed
DocumentProcessingFailed
```

Cloud-specific trigger handlers should convert provider events into these domain events or commands.

## Interface Implementation Matrix

| Interface | Current AWS implementation | Future Azure implementation | Business logic impact |
|---|---|---|---|
| `IStorageProvider` | `AwsS3StorageProvider` using S3 | `AzureBlobStorageProvider` using Blob Storage | None after `S3Key` becomes `StorageKey`. |
| `IDocumentProcessor` | `AwsDocumentProcessor` using Textract plus direct extraction | `AzureDocumentProcessor` using Document Intelligence plus direct extraction | None if both return `ExtractedDocument`. |
| `IEmbeddingProvider` | `AwsBedrockEmbeddingProvider` | `AzureOpenAiEmbeddingProvider` | None if embedding dimensions are configured per vector index. |
| `IChatProvider` | `AwsBedrockChatProvider` | `AzureOpenAiChatProvider` | None if prompts stay provider-neutral. |
| `IVectorStore` | `AwsOpenSearchVectorStore` | `AzureAiSearchVectorStore` | None if filters/search request are neutral. |
| `IIdentityProvider` | `AwsCognitoIdentityProvider` | `AzureEntraIdentityProvider` | Low, provided claims are normalized. |
| `IEventPublisher` | `AwsEventPublisher` using SNS/SQS/EventBridge | `AzureEventPublisher` using Event Grid/Service Bus/Queue Storage | None for application events. |

## Current Flow

### Current Upload and Ingestion Flow

```text
Angular Upload Panel
  -> Api.uploadDocument()
  -> DocumentsController.Upload()
  -> IStorageService.UploadAsync()
  -> S3StorageService.UploadAsync()
  -> Amazon S3
  -> IDocumentRepository.CreateUploadRecordAsync()
  -> DynamoDbDocumentRepository
  -> DynamoDB

S3 Object Created Event
  -> S3DocumentIngestionFunction.FunctionHandler(S3Event)
  -> AmazonS3Client.GetObjectAsync()
  -> TextExtractionService
     or TextractTextExtractionService
     or TextractAsyncExtractionService
  -> ChunkingService
  -> EmbeddingBatchService
  -> Bedrock Runtime
  -> ChunkPersistenceService
  -> DynamoDB
  -> OpenSearchService.IndexDocumentAsync()
  -> OpenSearch/AOSS
```

### Current Chat/RAG Flow

```text
Angular Chat Page
  -> Api.askQuestion()
  -> ChatController.Ask()
  -> ChatService.AskAsync()
  -> UserApprovalService.ResolveAccessAsync()
  -> DynamoDbConversationRepository.GetSessionAsync/GetMessagesAsync()
  -> RetrievalService.AskAsync()
  -> RedisCacheService.GetAsync()
  -> BedrockEmbeddingService.GenerateEmbeddingAsync()
  -> OpenSearchService.SearchAsync()
  -> DynamoDbChunkRepository fallback/hybrid ranking
  -> PromptBuilder.BuildGroundedPrompt()
  -> BedrockChatCompletionService.GenerateAnswerAsync()
  -> DynamoDbConversationRepository.AddMessageAsync()
  -> RedisCacheService.SetAsync()
  -> ChatAskResponse
```

## Target Flow

### Target Upload Flow

```text
Angular Upload Panel
  -> Api.uploadDocument()
  -> DocumentsController.Upload()
  -> IStorageProvider.UploadAsync()
       CloudProvider=AWS   -> AwsS3StorageProvider
       CloudProvider=Azure -> AzureBlobStorageProvider
  -> IDocumentRepository.CreateUploadRecordAsync()
       CloudProvider=AWS   -> DynamoDbDocumentRepository
       CloudProvider=Azure -> CosmosDocumentRepository
  -> IEventPublisher.PublishAsync(DocumentUploaded)
       CloudProvider=AWS   -> AwsEventPublisher
       CloudProvider=Azure -> AzureEventPublisher
```

### Target Ingestion Flow

```text
Cloud upload event adapter
  CloudProvider=AWS
    S3/Lambda event adapter
  CloudProvider=Azure
    Blob/Event Grid/Azure Functions event adapter
  |
  v
DocumentIngestionService.ProcessAsync(DocumentUploaded)
  -> IStorageProvider.OpenReadAsync()
  -> IDocumentProcessor.ExtractAsync()
  -> ChunkingService.CreateChunks()
  -> IEmbeddingProvider.GenerateEmbeddingAsync()
  -> IChunkRepository.SaveChunkAsync()
  -> IDocumentRepository.UpdateIngestionStatusAsync()
  -> IVectorStore.IndexAsync()
  -> IEventPublisher.PublishAsync(DocumentIndexed)
```

### Target Chat/RAG Flow

```text
Angular Chat Page
  -> Api.askQuestion()
  -> ChatController.Ask()
  -> ChatService.AskAsync()
  -> IIdentityProvider.ResolveUserAsync()
  -> ConversationRepository.GetSessionAsync/GetMessagesAsync()
  -> RetrievalService.AskAsync()
  -> Cache.GetAsync()
  -> IEmbeddingProvider.GenerateEmbeddingAsync()
       CloudProvider=AWS   -> AwsBedrockEmbeddingProvider
       CloudProvider=Azure -> AzureOpenAiEmbeddingProvider
  -> IVectorStore.SearchAsync()
       CloudProvider=AWS   -> AwsOpenSearchVectorStore
       CloudProvider=Azure -> AzureAiSearchVectorStore
  -> Repository fallback/hybrid ranking
       CloudProvider=AWS   -> DynamoDB repositories
       CloudProvider=Azure -> Cosmos DB repositories
  -> PromptBuilder.BuildGroundedPrompt()
  -> IChatProvider.GenerateAnswerAsync()
       CloudProvider=AWS   -> AwsBedrockChatProvider
       CloudProvider=Azure -> AzureOpenAiChatProvider
  -> ConversationRepository.AddMessageAsync()
  -> Cache.SetAsync()
  -> ChatAskResponse
```

## Target Architecture Diagram

```text
                         +-------------------+
                         |   Angular UI      |
                         | OIDC + API client |
                         +---------+---------+
                                   |
                                   v
                         +-------------------+
                         | ASP.NET Core API  |
                         | Controllers       |
                         +---------+---------+
                                   |
                                   v
                         +-------------------+
                         | Application Layer |
                         | Business Logic    |
                         +---------+---------+
                                   |
             +---------------------+----------------------+
             |                                            |
             v                                            v
   +---------------------+                    +----------------------+
   | Provider Interfaces |                    | Repository/Cache     |
   | Storage             |                    | Document             |
   | DocumentProcessor   |                    | Chunk                |
   | Embedding           |                    | Conversation         |
   | Chat                |                    | User                 |
   | VectorStore         |                    | Cache                |
   | Identity            |                    +----------+-----------+
   | Events              |                               |
   +----------+----------+                               |
              |                                          |
     CloudProvider switch                               |
              |                                          |
       +------+-------+                         +--------+-------+
       |              |                         |                |
       v              v                         v                v
 +------------+ +-------------+          +-------------+ +--------------+
 | AWS Module | | Azure Module|          | DynamoDB    | | Cosmos DB    |
 +------------+ +-------------+          | Redis       | | Redis        |
 | S3         | | Blob        |          +-------------+ +--------------+
 | Textract   | | Doc Intel   |
 | Bedrock    | | Azure OpenAI|
 | OpenSearch | | AI Search   |
 | Cognito    | | Entra ID    |
 | SNS/SQS    | | Event Grid  |
 +------------+ +-------------+
```

## Target Provider Selection Diagram

```text
Configuration
  CloudProvider = AWS
       |
       v
  AddAwsInfrastructure()
       |
       +-- IStorageProvider   = AwsS3StorageProvider
       +-- IDocumentProcessor = AwsDocumentProcessor
       +-- IEmbeddingProvider = AwsBedrockEmbeddingProvider
       +-- IChatProvider      = AwsBedrockChatProvider
       +-- IVectorStore       = AwsOpenSearchVectorStore
       +-- IIdentityProvider  = AwsCognitoIdentityProvider
       +-- IEventPublisher    = AwsEventPublisher

Configuration
  CloudProvider = Azure
       |
       v
  AddAzureInfrastructure()
       |
       +-- IStorageProvider   = AzureBlobStorageProvider
       +-- IDocumentProcessor = AzureDocumentProcessor
       +-- IEmbeddingProvider = AzureOpenAiEmbeddingProvider
       +-- IChatProvider      = AzureOpenAiChatProvider
       +-- IVectorStore       = AzureAiSearchVectorStore
       +-- IIdentityProvider  = AzureEntraIdentityProvider
       +-- IEventPublisher    = AzureEventPublisher
```

## Target Sequence Diagram: Upload and Ingestion

```text
User
  -> Angular UI: select file
  -> DocumentsController: POST /Documents/upload
  -> IStorageProvider: UploadAsync(file)
  -> IDocumentRepository: CreateUploadRecordAsync()
  -> IEventPublisher: Publish(DocumentUploaded)
  <- DocumentsController: UploadDocumentResponse

Cloud Event Adapter
  -> DocumentIngestionService: ProcessAsync(DocumentUploaded)
  -> IStorageProvider: OpenReadAsync(StorageObjectRef)
  -> IDocumentProcessor: ExtractAsync(document stream/ref)
  -> ChunkingService: CreateChunks()
  -> IEmbeddingProvider: GenerateEmbeddingAsync(chunk text)
  -> IChunkRepository: SaveChunkAsync()
  -> IVectorStore: IndexAsync(chunk)
  -> IDocumentRepository: MarkIndexedAsync()
  -> IEventPublisher: Publish(DocumentIndexed)
```

## Target Sequence Diagram: Chat and RAG

```text
User
  -> Angular UI: ask question
  -> ChatController: POST /Chat/ask
  -> ChatService: AskAsync()
  -> IIdentityProvider: ResolveUserAsync()
  -> IConversationRepository: Load session/history
  -> RetrievalService: AskAsync()
  -> ICacheProvider: GetAsync(cache key)
  -> IEmbeddingProvider: GenerateEmbeddingAsync(question)
  -> IVectorStore: SearchAsync(vector request)
  -> IChunkRepository: fallback load when needed
  -> RetrievalService: Hybrid rank chunks
  -> PromptBuilder: BuildGroundedPrompt()
  -> IChatProvider: GenerateAnswerAsync(prompt)
  -> RetrievalService: Build citations
  -> ICacheProvider: SetAsync(response)
  -> IConversationRepository: Save user and assistant messages
  <- ChatController: ChatAskResponse
```

## Target Configuration Shape

```json
{
  "CloudProvider": "AWS",
  "Storage": {
    "Provider": "AWS",
    "BucketOrContainerName": "aws-rag-chat-docs"
  },
  "Embedding": {
    "Provider": "AWS",
    "ModelId": "amazon.titan-embed-text-v2:0"
  },
  "Chat": {
    "Provider": "AWS",
    "ModelId": "amazon.nova-lite-v1:0"
  },
  "VectorStore": {
    "Provider": "AWS",
    "IndexName": "rag-index-v2"
  },
  "Identity": {
    "Provider": "AWS",
    "Authority": "https://cognito-idp.region.amazonaws.com/userPoolId",
    "ClientId": "client-id",
    "GroupsClaim": "cognito:groups"
  }
}
```

```json
{
  "CloudProvider": "Azure",
  "Storage": {
    "Provider": "Azure",
    "BucketOrContainerName": "rag-documents"
  },
  "Embedding": {
    "Provider": "Azure",
    "DeploymentName": "embedding-deployment"
  },
  "Chat": {
    "Provider": "Azure",
    "DeploymentName": "chat-deployment"
  },
  "VectorStore": {
    "Provider": "Azure",
    "IndexName": "rag-index"
  },
  "Identity": {
    "Provider": "Azure",
    "Authority": "https://login.microsoftonline.com/tenant-id/v2.0",
    "ClientId": "client-id",
    "GroupsClaim": "groups"
  }
}
```

## Business Logic That Must Remain Cloud-Neutral

The following should not reference AWS or Azure SDKs, service names, request models, or provider-specific configuration:

- Controllers
- `ChatService`
- `RetrievalService`
- `ConversationService`
- `UserApprovalService`
- `AdminAnalyticsService`
- `PromptBuilder`
- `QueryIntentClassifier`
- `ResponsePlanner`
- `ChunkingService`
- `DocumentIngestionService`
- Domain entities
- DTOs returned to the frontend

## Required Renames for Neutral Architecture

| Current term | Target term |
|---|---|
| `S3Key` | `StorageKey` or `ObjectKey` |
| `S3StorageService` | `AwsS3StorageProvider` |
| `BedrockEmbeddingService` | `AwsBedrockEmbeddingProvider` |
| `BedrockChatCompletionService` | `AwsBedrockChatProvider` |
| `OpenSearchService` | `AwsOpenSearchVectorStore` |
| `CognitoUserRoleService` | `AwsCognitoIdentityProvider` or `AwsCognitoRoleProvider` |
| `TextractTextExtractionService` | AWS-specific part of `AwsDocumentProcessor` |
| `DynamoDb*Repository` | AWS-specific repository implementations |
| `RedisCacheService` | `RedisCacheProvider` or `ICacheProvider` implementation |

## Migration Target End State

At the end of the migration, the platform should support:

```text
CloudProvider=AWS
  API + Application logic unchanged
  AWS infrastructure selected through DI
  AWS event adapters activate ingestion

CloudProvider=Azure
  API + Application logic unchanged
  Azure infrastructure selected through DI
  Azure event adapters activate ingestion
```

The only expected differences between providers should be:

- Configuration values.
- Registered infrastructure implementations.
- Deployment/runtime host for ingestion adapters.
- Cloud resource provisioning.

The RAG workflow, document lifecycle, prompt behavior, access rules, conversation flow, admin workflows, and frontend API contract should remain stable across both providers.
