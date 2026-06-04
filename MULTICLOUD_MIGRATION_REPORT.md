# Multi-Cloud Migration Inventory Report

## 1. Current Architecture

### API Layer

The API layer is an ASP.NET Core .NET 8 Web API in `AwsRagChat/AwsRagChat.Api`.

- `Program.cs`
  - Configures logging, CORS, forwarded headers, Redis cache, controllers, Swagger, authentication, authorization, and cloud-provider infrastructure registration.
  - Calls `AddCloudProviderAuthentication(configuration)` and `AddCloudProviderInfrastructure(configuration)`.
  - Current provider switch supports only `CloudProvider = AWS`.
- Controllers:
  - `Controllers/DocumentsController.cs`
    - Uploads documents through `IStorageService`.
    - Creates document metadata through `IDocumentRepository`.
    - Exposes document listing and lookup.
  - `Controllers/ChatController.cs`
    - Accepts chat questions and delegates to `ChatService`.
  - `Controllers/ConversationsController.cs`
    - Manages conversation sessions and messages through `ConversationService`.
  - `Controllers/AdminController.cs`
    - Exposes dashboard, user approval, document monitoring, preview URL, conversation analytics, and ingestion status.
  - `Controllers/AuthController.cs`
    - Registers users through user/approval services.
  - `Controllers/UserRoleController.cs`
    - Reads and assigns user roles through `IUserRoleService`.
- Security:
  - `Security/ClaimsPrincipalExtensions.cs`
    - Extracts Cognito group claims using `cognito:groups`.

### Application Layer

The application layer is in `AwsRagChat/AwsRagChat.Application`.

- Interfaces:
  - `IStorageService`
  - `IEmbeddingService`
  - `IChatCompletionService`
  - `IVectorSearchService`
  - `IChunkRepository`
  - `IDocumentRepository`
  - `IConversationRepository`
  - `IUserRepository`
  - `IUserRoleService`
  - `IRedisCacheService`
  - `IAdminAnalyticsService`
  - `IUserApprovalService`
- Services:
  - `ChatService`
    - Main chat orchestration service.
    - Performs access checks, intent classification, metadata answers, image/preview handling, RAG routing, and conversation persistence.
  - `RetrievalService`
    - Performs cache lookup, query embedding, vector search, DynamoDB fallback ranking, prompt/LLM invocation, citation extraction, and response caching.
  - `PromptBuilder`
    - Builds grounded, general assistant, and knowledge overview prompts.
  - `ConversationService`
    - Manages conversation sessions/messages.
  - `UserApprovalService`
    - Resolves approved user role/status.
  - `AdminAnalyticsService`
    - Aggregates document/user/conversation stats.
  - `QueryIntentClassifier`
    - Classifies greeting, metadata, knowledge overview, and general/RAG intents.
  - `ResponsePlanner`
    - Plans text/table/chart/image/RAG response routes.

### Infrastructure Layer

The infrastructure layer is in `AwsRagChat/AwsRagChat.Infrastructure`.

- AWS provider registration:
  - `DependencyInjection/CloudProviderServiceCollectionExtensions.cs`
    - Switches on `CloudProvider`.
    - Supports only `AWS`.
  - `Aws/AwsProviderServiceCollectionExtensions.cs`
    - Registers AWS SDK clients and AWS-backed services.
  - `Aws/AwsAuthenticationExtensions.cs`
    - Configures Cognito JWT bearer auth.
- AWS implementations:
  - `Storage/S3StorageService.cs`
    - Implements `IStorageService` using S3.
  - `AI/BedrockEmbeddingService.cs`
    - Implements `IEmbeddingService` using Amazon Bedrock.
  - `AI/BedrockChatCompletionService.cs`
    - Implements `IChatCompletionService` using Amazon Bedrock Nova.
  - `Services/OpenSearchService.cs`
    - Implements `IVectorSearchService` using OpenSearch/AOSS with AWS SigV4.
  - `Persistence/DynamoDbChunkRepository.cs`
    - Implements `IChunkRepository`.
  - `Persistence/DynamoDbDocumentRepository.cs`
    - Implements `IDocumentRepository`.
  - `Persistence/DynamoDbConversationRepository.cs`
    - Implements `IConversationRepository`.
  - `Persistence/DynamoDbUserRepository.cs`
    - Implements `IUserRepository`.
  - `Services/CognitoUserRoleService.cs`
    - Implements `IUserRoleService`.
  - `Cache/RedisCacheService.cs`
    - Implements `IRedisCacheService` over `IDistributedCache`.

### Ingestion Layer

The ingestion layer is in `AwsRagChat/AwsRagChat.Ingestion`.

- AWS Lambda handlers:
  - `Handlers/S3DocumentIngestionFunction.cs`
    - Receives S3 events.
    - Reads uploaded object from S3.
    - Performs direct extraction or Textract OCR routing.
    - Runs chunking, embeddings, DynamoDB persistence, and OpenSearch indexing.
  - `Handlers/TextractCompletionFunction.cs`
    - Receives SQS events containing Textract completion notifications, including SNS envelope parsing.
    - Fetches Textract async results.
    - Continues ingestion pipeline.
- Ingestion services:
  - `TextExtractionService`
    - Direct extraction for text/CSV/PDF.
  - `TextractTextExtractionService`
    - Synchronous Textract OCR.
  - `TextractAsyncExtractionService`
    - Starts and reads asynchronous Textract jobs.
  - `ChunkingService`
    - Splits extracted documents into chunks.
  - `EmbeddingBatchService`
    - Calls Bedrock embeddings for every chunk.
  - `ChunkPersistenceService`
    - Writes chunks to DynamoDB.
  - `DocumentStatusService`
    - Updates document ingestion status in DynamoDB.
  - `DocumentIngestionPipeline`
    - Coordinates chunking, embedding, DynamoDB persistence, and OpenSearch indexing.

The ingestion layer is the least provider-agnostic subsystem because Lambda handlers manually create AWS SDK clients and depend on AWS event types.

### Angular Frontend

The frontend is an Angular app in `aws-rag-chat-ui`.

- `src/app/api.ts`
  - Wraps backend API calls for upload, document listing, conversation management, and chat.
  - DTOs expose AWS-specific `s3Key`.
- `src/app/auth.ts`
  - Implements Cognito Hosted UI OAuth PKCE flow.
  - Reads `cognito:groups` claims.
  - Calls Cognito `/oauth2/authorize`, `/oauth2/token`, and `/logout`.
- `src/environments/environment.ts`
  - Contains `apiBaseUrl`, `cognitoClientId`, `cognitoDomain`, and `cognitoScopes`.
- `src/app/app.html` and `src/app/pages/chat-page/chat-page.html`
  - User-facing text references Cognito, Amazon Bedrock, OpenSearch, and Titan Embeddings.

## 2. AWS Dependency Inventory

### S3

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Infrastructure/Storage/S3StorageService.cs` | `S3StorageService` | Constructor | S3 via `IAmazonS3` | Inject S3 client. |
| `AwsRagChat.Infrastructure/Storage/S3StorageService.cs` | `S3StorageService` | `UploadAsync` | S3 `PutObjectAsync` | Upload user documents to configured S3 bucket. |
| `AwsRagChat.Infrastructure/Storage/S3StorageService.cs` | `S3StorageService` | `CreateReadUrlAsync` | S3 presigned URL | Generate temporary read URL for uploaded images/PDF previews. |
| `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs` | `AwsProviderServiceCollectionExtensions` | `AddAwsProviderInfrastructure` | S3 | Registers `IAmazonS3`. |
| `AwsRagChat.Api/Controllers/DocumentsController.cs` | `DocumentsController` | `Upload` | S3 indirectly through `IStorageService` | Uploads files to storage and returns `S3Key`. |
| `AwsRagChat.Api/Controllers/AdminController.cs` | `AdminController` | preview URL endpoint | S3 indirectly through `IStorageService` | Creates read URL for document preview. |
| `AwsRagChat.Application/Services/ChatService.cs` | `ChatService` | `CreateSafeReadUrlAsync` | S3 indirectly through `IStorageService` | Creates read URL for image/figure extraction responses. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | Constructor | S3 via `AmazonS3Client` | Creates AWS S3 client manually. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | `FunctionHandler` | S3 event and `GetObjectAsync` | Processes S3 object-created events and reads object content. |
| `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs` | `TextractTextExtractionService` | `ExtractFromS3Async` | S3 location inside Textract request | Passes S3 bucket/key to Textract OCR. |
| `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs` | `TextractAsyncExtractionService` | `StartDocumentTextDetectionAsync` | S3 location inside Textract request | Starts async OCR against an S3 object. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `TryReadDocumentLocation` | S3 location from Textract message | Reads `S3Bucket` and `S3ObjectName` from Textract completion payload. |

### Bedrock

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs` | `BedrockEmbeddingService` | Constructor | Bedrock Runtime via `IAmazonBedrockRuntime` | Inject Bedrock runtime client. |
| `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs` | `BedrockEmbeddingService` | `GenerateEmbeddingAsync` | Bedrock `InvokeModelAsync` | Generate normalized text embeddings. |
| `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs` | `BedrockChatCompletionService` | Constructor | Bedrock Runtime via `IAmazonBedrockRuntime` | Inject Bedrock runtime client. |
| `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs` | `BedrockChatCompletionService` | `GenerateAnswerAsync` | Bedrock Nova model | Generate grounded RAG answer. |
| `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs` | `BedrockChatCompletionService` | `GenerateGeneralAnswerAsync` | Bedrock Nova model | Generate non-RAG assistant answer. |
| `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs` | `BedrockChatCompletionService` | `GenerateKnowledgeOverviewAsync` | Bedrock Nova model | Generate enterprise knowledge overview. |
| `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs` | `BedrockChatCompletionService` | `InvokeNovaAsync` | Bedrock `InvokeModelAsync` | Low-level Bedrock request/response handling. |
| `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs` | `AwsProviderServiceCollectionExtensions` | `AddAwsProviderInfrastructure` | Bedrock Runtime | Registers `IAmazonBedrockRuntime`. |
| `AwsRagChat.Ingestion/Services/EmbeddingBatchService.cs` | `EmbeddingBatchService` | Constructor | Bedrock Runtime via `IAmazonBedrockRuntime` | Inject Bedrock runtime client. |
| `AwsRagChat.Ingestion/Services/EmbeddingBatchService.cs` | `EmbeddingBatchService` | `AddEmbeddingsAsync` | Bedrock indirectly | Adds embeddings to all chunks. |
| `AwsRagChat.Ingestion/Services/EmbeddingBatchService.cs` | `EmbeddingBatchService` | `GenerateEmbeddingAsync` | Bedrock `InvokeModelAsync` | Generates chunk embeddings during ingestion. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | Constructor | Bedrock via `AmazonBedrockRuntimeClient` | Manually creates Bedrock client for ingestion. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | Constructor | Bedrock via `AmazonBedrockRuntimeClient` | Manually creates Bedrock client for async OCR completion ingestion. |

### OpenSearch

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Infrastructure/Services/OpenSearchService.cs` | `OpenSearchService` | Constructor | OpenSearch/AOSS with AWS SigV4 | Creates OpenSearch client using `AwsSigV4HttpConnection`, AWS region, and AWS credentials. |
| `AwsRagChat.Infrastructure/Services/OpenSearchService.cs` | `OpenSearchService` | `IndexDocumentAsync` | OpenSearch | Indexes document chunks with embeddings. |
| `AwsRagChat.Infrastructure/Services/OpenSearchService.cs` | `OpenSearchService` | `SearchAsync` | OpenSearch kNN | Performs vector search with role/document filters. |
| `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs` | `AwsProviderServiceCollectionExtensions` | `AddAwsProviderInfrastructure` | OpenSearch | Registers `OpenSearchService`. |
| `AwsRagChat.Application/Services/RetrievalService.cs` | `RetrievalService` | `AskAsync` | OpenSearch indirectly through `IVectorSearchService` | Executes vector retrieval before hybrid ranking. |
| `AwsRagChat.Ingestion/Services/DocumentIngestionPipeline.cs` | `DocumentIngestionPipeline` | Constructor | OpenSearch concrete service | Depends directly on `OpenSearchService`. |
| `AwsRagChat.Ingestion/Services/DocumentIngestionPipeline.cs` | `DocumentIngestionPipeline` | `ProcessExtractedDocumentAsync` | OpenSearch concrete service | Indexes every embedded chunk. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | Constructor | OpenSearch concrete service | Manually creates `OpenSearchService`. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | Constructor | OpenSearch concrete service | Manually creates `OpenSearchService`. |

### DynamoDB

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs` | `DynamoDbChunkRepository` | Constructor | DynamoDB via `IAmazonDynamoDB` | Injects DynamoDB client. |
| `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs` | `DynamoDbChunkRepository` | `SaveChunkAsync` | DynamoDB `PutItemAsync` | Persists chunk records. |
| `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs` | `DynamoDbChunkRepository` | chunk query/list methods | DynamoDB `ScanAsync`, `QueryAsync` | Loads chunks for retrieval fallback and document scope. |
| `AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs` | `DynamoDbDocumentRepository` | document CRUD/query methods | DynamoDB | Persists and queries document metadata/status. |
| `AwsRagChat.Infrastructure/Persistence/DynamoDbConversationRepository.cs` | `DynamoDbConversationRepository` | session/message methods | DynamoDB | Persists conversation sessions and messages. |
| `AwsRagChat.Infrastructure/Persistence/DynamoDbUserRepository.cs` | `DynamoDbUserRepository` | user methods | DynamoDB | Persists users and approval metadata. |
| `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs` | `AwsProviderServiceCollectionExtensions` | `AddAwsProviderInfrastructure` | DynamoDB | Registers `IAmazonDynamoDB`. |
| `AwsRagChat.Ingestion/Services/ChunkPersistenceService.cs` | `ChunkPersistenceService` | Constructor | DynamoDB via `IAmazonDynamoDB` | Injects DynamoDB client. |
| `AwsRagChat.Ingestion/Services/ChunkPersistenceService.cs` | `ChunkPersistenceService` | `SaveChunksAsync` | DynamoDB `PutItemAsync` | Persists chunk records during ingestion. |
| `AwsRagChat.Ingestion/Services/DocumentStatusService.cs` | `DocumentStatusService` | status methods | DynamoDB | Updates upload, processing, OCR, embedding, indexing, indexed, failed status. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | Constructor | DynamoDB via `AmazonDynamoDBClient` | Manually creates DynamoDB client. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | Constructor | DynamoDB via `AmazonDynamoDBClient` | Manually creates DynamoDB client. |

### Cognito

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Infrastructure/Aws/AwsAuthenticationExtensions.cs` | `AwsAuthenticationExtensions` | `AddAwsCognitoAuthentication` | Cognito JWT authority | Configures JWT bearer validation against Cognito authority/audience. |
| `AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs` | `AwsProviderServiceCollectionExtensions` | `AddAwsProviderInfrastructure` | Cognito Identity Provider | Registers `IAmazonCognitoIdentityProvider`. |
| `AwsRagChat.Infrastructure/DependencyInjection.cs` | `DependencyInjection` | `AddInfrastructure` | Cognito Identity Provider | Registers Cognito client and `CognitoUserRoleService`. |
| `AwsRagChat.Infrastructure/Services/CognitoUserRoleService.cs` | `CognitoUserRoleService` | role methods | Cognito Identity Provider | Reads and assigns user roles/groups in Cognito. |
| `AwsRagChat.Api/Security/ClaimsPrincipalExtensions.cs` | `ClaimsPrincipalExtensions` | `GetRoles`, `GetFirstRole` | Cognito claim `cognito:groups` | Extracts role/group claims from Cognito JWT. |
| `aws-rag-chat-ui/src/app/auth.ts` | `Auth` | `login` | Cognito Hosted UI | Builds Cognito OAuth authorize URL. |
| `aws-rag-chat-ui/src/app/auth.ts` | `Auth` | `logout` | Cognito Hosted UI | Builds Cognito logout URL. |
| `aws-rag-chat-ui/src/app/auth.ts` | `Auth` | `exchangeCodeForTokens` | Cognito token endpoint | Exchanges OAuth code for tokens. |
| `aws-rag-chat-ui/src/app/auth.ts` | `Auth` | `storeSession`, `restoreSessionFromStorage` | Cognito JWT claims | Reads `cognito:groups` from access/id tokens. |

### Textract

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs` | `TextractTextExtractionService` | Constructor | Textract via `IAmazonTextract` | Injects Textract client. |
| `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs` | `TextractTextExtractionService` | `CanExtractWithTextract` | Textract capability assumption | Determines if image file can use Textract OCR. |
| `AwsRagChat.Ingestion/Services/TextractTextExtractionService.cs` | `TextractTextExtractionService` | `ExtractFromS3Async` | Textract `DetectDocumentTextAsync` | Performs synchronous OCR over S3 object. |
| `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs` | `TextractAsyncExtractionService` | Constructor | Textract via `IAmazonTextract` | Injects Textract client. |
| `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs` | `TextractAsyncExtractionService` | `StartDocumentTextDetectionAsync` | Textract `StartDocumentTextDetectionAsync` | Starts async OCR, publishing completion to SNS. |
| `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs` | `TextractAsyncExtractionService` | `GetCompletedDocumentAsync` | Textract `GetDocumentTextDetectionAsync` | Retrieves completed OCR results. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | Constructor | Textract via `AmazonTextractClient` | Manually creates Textract client. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | `FunctionHandler` | Textract services | Falls back to sync/async OCR when needed. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | Constructor | Textract via `AmazonTextractClient` | Manually creates Textract client. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `FunctionHandler` | Textract completion message/results | Processes completed OCR job. |

### Lambda

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Ingestion/AssemblyInfo.cs` | Assembly metadata | Attribute | Lambda serializer | Configures Lambda JSON serializer. |
| `AwsRagChat.Ingestion/aws-lambda-tools-defaults.json` | N/A | N/A | Lambda deployment config | Declares Lambda handler entry point. |
| `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs` | `S3DocumentIngestionFunction` | `FunctionHandler(S3Event, ILambdaContext)` | Lambda runtime | S3-triggered ingestion function. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `FunctionHandler(SQSEvent, ILambdaContext)` | Lambda runtime | SQS-triggered Textract completion function. |

### SNS

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Ingestion/Services/TextractAsyncExtractionService.cs` | `TextractAsyncExtractionService` | `StartDocumentTextDetectionAsync` | SNS | Provides `SNSTopicArn` for Textract completion notification. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `TryGetTextractMessageJson` | SNS envelope | Parses SNS `Message` wrapper inside SQS body. |
| `AwsRagChat.Ingestion/appsettings.json` | N/A | N/A | SNS | Configures `TextractAsync:SnsTopicArn`. |

### SQS

| File path | Class | Method | AWS service used | Purpose |
|---|---|---|---|---|
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `FunctionHandler(SQSEvent, ILambdaContext)` | SQS | Receives Textract completion messages. |
| `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs` | `TextractCompletionFunction` | `TryGetTextractMessageJson` | SQS message body | Parses raw SQS body and optional SNS envelope. |

## 3. Existing Abstractions

| Interface/abstraction | File path | Current implementation | Status | Notes |
|---|---|---|---|---|
| `IStorageService` | `AwsRagChat.Application/Interfaces/IStorageService.cs` | `S3StorageService` | Needs modification | Interface is reusable, but method/DTO usage still returns `S3Key` terminology outside the interface. |
| `IEmbeddingService` | `AwsRagChat.Application/Interfaces/IEmbeddingService.cs` | `BedrockEmbeddingService` | Reusable | Good provider boundary for API retrieval. Ingestion does not reuse it. |
| `IChatCompletionService` | `AwsRagChat.Application/Interfaces/IChatCompletionService.cs` | `BedrockChatCompletionService` | Reusable | Good provider boundary for API chat. Prompt format may need provider-specific tuning. |
| `IVectorSearchService` | `AwsRagChat.Application/Interfaces/IVectorSearchService.cs` | `OpenSearchService` | Needs modification | Useful boundary, but ingestion pipeline depends on concrete `OpenSearchService`. |
| `IChunkRepository` | `AwsRagChat.Application/Interfaces/IChunkRepository.cs` | `DynamoDbChunkRepository` | Reusable | API retrieval uses abstraction. Ingestion uses separate DynamoDB-specific `ChunkPersistenceService`. |
| `IDocumentRepository` | `AwsRagChat.Application/Interfaces/IDocumentRepository.cs` | `DynamoDbDocumentRepository` | Needs modification | Good boundary, but `ExistingDocumentRecord` exposes `S3Key`. |
| `IConversationRepository` | `AwsRagChat.Application/Interfaces/IConversationRepository.cs` | `DynamoDbConversationRepository` | Reusable | Good boundary for provider-specific persistence replacement. |
| `IUserRepository` | `AwsRagChat.Application/Interfaces/IUserRepository.cs` | `DynamoDbUserRepository` | Reusable | Good persistence boundary. |
| `IUserRoleService` | `AwsRagChat.Application/Interfaces/IUserRoleService.cs` | `CognitoUserRoleService` | Needs modification | Boundary exists, but auth/claims are Cognito-specific. |
| `IUserApprovalService` | `AwsRagChat.Application/Interfaces/IUserRepository.cs` | `UserApprovalService` | Reusable | Business service uses repositories and can remain cloud-neutral. |
| `IRedisCacheService` | `AwsRagChat.Application/Interfaces/IRedisCacheService.cs` | `RedisCacheService` | Needs modification | Redis is portable, but name should become `ICacheService` or similar for provider neutrality. |
| `IAdminAnalyticsService` | `AwsRagChat.Application/Interfaces/IAdminAnalyticsService.cs` | `AdminAnalyticsService` | Reusable | Business aggregation boundary. |
| `CloudProviderServiceCollectionExtensions` | `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs` | AWS-only switch | Needs modification | Good starting point for provider-based DI, but currently throws for non-AWS. |

## 4. AWS Leakage

### `S3Key`

AWS-specific storage naming appears in domain entities, DTOs, repositories, frontend contracts, prompts, logs, and search metadata.

- `AwsRagChat.Domain/Entities/DocumentChunk.cs`
  - Property: `DocumentChunk.S3Key`
- `AwsRagChat.Application/Interfaces/IDocumentRepository.cs`
  - Class: `ExistingDocumentRecord`
  - Property: `S3Key`
- `AwsRagChat.Application/DTOs/UploadDocumentResponse.cs`
  - Property: `S3Key`
- `AwsRagChat.Application/Services/ChatService.cs`
  - Method: `CreateSafeReadUrlAsync`
  - Uses `document.S3Key`.
- `AwsRagChat.Application/Services/RetrievalService.cs`
  - Method: `ComputeDocumentKeywordScore`
  - Includes `document.S3Key` in searchable metadata.
- `AwsRagChat.Application/Services/PromptBuilder.cs`
  - Prompt says not to mention `S3 keys`.
- `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs`
  - Maps DynamoDB attribute `S3Key`.
- `AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs`
  - Maps and persists `S3Key`.
- `AwsRagChat.Ingestion/Services/ChunkPersistenceService.cs`
  - Writes DynamoDB attribute `S3Key`.
- `AwsRagChat.Ingestion/Services/DocumentStatusService.cs`
  - Updates document status with `S3Key`.
- `aws-rag-chat-ui/src/app/api.ts`
  - Interfaces `UploadResponse` and `DocumentMetadata` expose `s3Key`.

Recommended neutral term: `StorageKey` or `ObjectKey`.

### DynamoDB-specific Models and Shapes

- `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs`
  - Uses `Dictionary<string, AttributeValue>`, `PutItemRequest`, `ScanRequest`, `QueryRequest`, and DynamoDB key/query expressions.
- `AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs`
  - Uses DynamoDB attribute maps, update expressions, scans, and query expressions.
- `AwsRagChat.Infrastructure/Persistence/DynamoDbConversationRepository.cs`
  - Uses DynamoDB item maps for sessions, messages, and citations.
- `AwsRagChat.Infrastructure/Persistence/DynamoDbUserRepository.cs`
  - Uses DynamoDB item maps for users.
- `AwsRagChat.Ingestion/Services/ChunkPersistenceService.cs`
  - Writes chunks directly with DynamoDB `PutItemRequest`.
- `AwsRagChat.Ingestion/Services/DocumentStatusService.cs`
  - Updates document records directly with DynamoDB expressions.
- `AwsRagChat.Application/Services/RetrievalService.cs`
  - Logs and fallback naming refer to DynamoDB directly even though calls go through `IChunkRepository`.

### Cognito-specific Claims

- `AwsRagChat.Api/Security/ClaimsPrincipalExtensions.cs`
  - Reads `cognito:groups`.
- `aws-rag-chat-ui/src/app/auth.ts`
  - `JwtPayload` defines `'cognito:groups'`.
  - `storeSession` and `restoreSessionFromStorage` read `cognito:groups`.
  - Error text references Cognito token endpoint.
- `aws-rag-chat-ui/src/environments/environment.ts`
  - Uses `cognitoClientId`, `cognitoDomain`, and `cognitoScopes`.
- `aws-rag-chat-ui/src/app/app.html`
  - Displays `Login with Cognito`.
- `aws-rag-chat-ui/src/app/pages/chat-page/chat-page.html`
  - Displays `Login with Cognito`.

Recommended neutral concepts: `AuthProvider`, `Authority`, `ClientId`, `Scopes`, `RoleClaim`, `GroupsClaim`.

### OpenSearch-specific DTOs/Terminology

No public DTO named specifically for OpenSearch was found, but OpenSearch terminology leaks into services, logs, variable names, ranking names, and configuration.

- `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`
  - Concrete vector store implementation and OpenSearch request shape.
- `AwsRagChat.Application/Services/RetrievalService.cs`
  - Logs `OpenSearchStart`, `OpenSearchEnd`, `OpenSearchHits`.
  - Uses local ranking names `OpenSearchRankBoost` and `OpenSearchBoost`.
  - Logs fallback from OpenSearch to DynamoDB.
- `AwsRagChat.Ingestion/Services/DocumentIngestionPipeline.cs`
  - Field `_openSearchService`.
  - Logs `Indexing chunks into OpenSearch`.
- `AwsRagChat.Api/appsettings.json`
  - Section `OpenSearch`.
- `AwsRagChat.Ingestion/appsettings.json`
  - Section `OpenSearch`.
- `aws-rag-chat-ui/src/app/app.html`
  - Displays `OpenSearch`.

Recommended neutral concepts: `VectorStore`, `VectorSearch`, `VectorHits`, `SemanticRankBoost`.

### Bedrock-specific Naming

- `AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs`
  - Class and log messages use Bedrock.
- `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`
  - Class, method `InvokeNovaAsync`, and logs use Bedrock/Nova.
- `AwsRagChat.Infrastructure/Options/BedrockOptions.cs`
  - Config section name is `Bedrock`.
- `AwsRagChat.Ingestion/Options/BedrockOptions.cs`
  - Duplicate ingestion-specific Bedrock options.
- `AwsRagChat.Ingestion/Services/EmbeddingBatchService.cs`
  - Uses `BedrockOptions`.
- `AwsRagChat.Ingestion/Handlers/S3DocumentIngestionFunction.cs`
  - Variable `bedrock`.
- `AwsRagChat.Ingestion/Handlers/TextractCompletionFunction.cs`
  - Variable `bedrock`.
- `AwsRagChat.Application/Services/RetrievalService.cs`
  - Logs `BedrockGenerationStart` and `BedrockGenerationEnd`.
- `AwsRagChat.Api/appsettings.json`
  - Section `Bedrock`.
- `AwsRagChat.Ingestion/appsettings.json`
  - Section `Bedrock`.
- `aws-rag-chat-ui/src/app/app.html`
  - Displays `Amazon Bedrock` and `Titan Embeddings`.

Recommended neutral concepts: `EmbeddingProvider`, `ChatProvider`, `ModelProvider`, `ChatModelId`, `EmbeddingModelId`.

## 5. Multi-Cloud Readiness

| Subsystem | Score | Assessment |
|---|---:|---|
| Storage | 6/10 | `IStorageService` already exists and API uses it. Leakage remains through `S3Key`, S3 config, S3-specific messages, and ingestion S3 event/client usage. |
| Embeddings | 6/10 | API retrieval uses `IEmbeddingService`. Ingestion uses `EmbeddingBatchService` with direct Bedrock dependency instead of the shared abstraction. |
| Chat | 7/10 | `IChatCompletionService` is a good boundary. Bedrock/Nova naming and payload shape are isolated mostly in infrastructure, but logs/config remain Bedrock-specific. |
| Vector Search | 5/10 | `IVectorSearchService` exists for API retrieval. Ingestion directly depends on `OpenSearchService`, and application logs/ranking names mention OpenSearch. |
| Persistence | 5/10 | Repository interfaces exist for API/application flows. Ingestion has direct DynamoDB services, DynamoDB item maps, and status update logic outside repository abstractions. |
| Identity | 3/10 | Backend has `IUserRoleService`, but auth registration, claims extraction, frontend OAuth flow, environment names, and UI text are Cognito-specific. |
| Ingestion | 2/10 | Lambda, S3 events, SQS events, SNS envelopes, Textract, Bedrock, DynamoDB, and OpenSearch are manually wired in constructors. This subsystem needs the largest refactor. |

Overall readiness: **4.9/10**.

## 6. Refactoring Order

### Priority 1: Establish Provider-Neutral Vocabulary

1. Replace new usage of `S3Key` with `StorageKey` or `ObjectKey`.
2. Introduce neutral config concepts:
   - `Storage`
   - `EmbeddingProvider`
   - `ChatProvider`
   - `VectorStore`
   - `Persistence`
   - `Identity`
   - `DocumentProcessing`
3. Keep backward compatibility for current AWS config while preparing provider-specific child sections.
4. Rename logs and internal ranking terms from `OpenSearch*` and `Bedrock*` to `VectorSearch*` and `Generation*`.

### Priority 2: Fix Ingestion Boundaries

1. Convert `DocumentIngestionPipeline` dependencies to interfaces:
   - embedding provider
   - chunk repository/persistence
   - document status service
   - vector search/index service
2. Extract `IDocumentStatusService` from `DocumentStatusService`.
3. Reuse `IEmbeddingService` in ingestion instead of `EmbeddingBatchService` directly tied to Bedrock.
4. Reuse `IChunkRepository` or create an ingestion-safe chunk writer abstraction instead of `ChunkPersistenceService`.
5. Remove direct `OpenSearchService` dependency from ingestion pipeline.

### Priority 3: Separate Trigger Adapters from Ingestion Use Case

1. Keep cloud-specific event handlers thin.
2. Move ingestion orchestration into a provider-neutral application service.
3. Convert `S3DocumentIngestionFunction.FunctionHandler` into an AWS adapter that maps `S3Event` to a neutral document-ingestion request.
4. Convert `TextractCompletionFunction.FunctionHandler` into an AWS adapter that maps `SQSEvent`/SNS envelope to a neutral OCR-completion request.
5. Introduce neutral concepts for upload event, OCR job completion, document location, and object content reader.

### Priority 4: Isolate Document Processing

1. Create a provider-neutral document processing abstraction.
2. Keep direct text/PDF/CSV extraction provider-neutral.
3. Place Textract sync/async behavior behind AWS document-processing implementation.
4. Prepare Azure Document Intelligence as a future implementation, without adding it during this inventory phase.

### Priority 5: Identity and Claims Normalization

1. Introduce provider-neutral role/group claim mapping in backend configuration.
2. Replace direct `cognito:groups` extraction with configured claim names.
3. Make frontend auth environment provider-neutral.
4. Move Cognito Hosted UI logic behind an auth-provider strategy or configuration model.
5. Remove hardcoded Cognito UI text.

### Priority 6: Provider-Specific DI Modules

1. Keep `CloudProviderServiceCollectionExtensions` as the central provider switch.
2. Split AWS registrations into a clearly AWS-specific module.
3. Add extension points for Azure provider registration later.
4. Ensure application services depend only on application interfaces.
5. Ensure provider-specific packages do not leak into application/domain projects.

### Priority 7: Deployment/IaC Planning

1. Current API Dockerfile is portable and can remain.
2. Lambda deployment configuration is AWS-specific and should be isolated as an AWS deployment artifact.
3. Add future deployment structure for cloud-specific infrastructure without mixing provider logic into business code.
4. Keep Redis endpoint configurable because Redis itself is portable across AWS ElastiCache, Azure Cache for Redis, and self-hosted Redis.

## Summary

The repository already has useful application-layer abstractions for storage, embeddings, chat completion, vector search, repositories, cache, and user role services. The API/RAG runtime is therefore moderately ready for provider substitution.

The primary blockers are:

- AWS-specific ingestion handlers and direct AWS client construction.
- `S3Key` storage vocabulary leaking into domain, DTOs, frontend, prompts, and persistence.
- Cognito-specific frontend/backend auth flow and role claims.
- Direct DynamoDB helper services in ingestion.
- Direct OpenSearch concrete dependency in ingestion.
- Bedrock/OpenSearch/DynamoDB naming in application logs and internal ranking terminology.

Recommended migration direction: first neutralize vocabulary and ingestion abstractions, then split provider-specific DI modules, then add AWS/Azure implementations behind the same application contracts.
