# PHASE6_COMMIT4_POLLY_RESILIENCE_PLAN_V2

## Executive Summary

To ensure high availability and self-healing capabilities in production, this plan introduces a centralized retry and resilience strategy using **Polly v8** (the modern, high-performance, non-allocating .NET resilience engine).

The platform communicates with numerous external dependencies (SaaS, IaaS, and PaaS endpoints) over the network. Under load, these dependencies can fail transiently due to network glitches, rate-limiting (throttling), or temporary server-side degradation.

This plan details the transient error classification, circuit breaker settings, timeouts, and custom metrics that will be applied to the cloud infrastructure and ingestion layers. It incorporates codebase-specific findings (such as using `ClientResultException` for Azure OpenAI and resolving OpenSearch exception wrapping) and isolates all resilience logic inside the Infrastructure and Ingestion layers, leaving the core Application and Domain layers completely untouched.

---

## Updated Retry Matrix

Transient errors are retried using exponential backoff with jitter to prevent herd effects. Non-transient errors (such as authentication failures, authorization errors, or validation errors) fail fast immediately.

| Dependency / Service | Base Exception Type | Retryable Failures (Retried) | Non-Retryable Failures (Fast Fail) | Backoff & Jitter Strategy |
| :--- | :--- | :--- | :--- | :--- |
| **AWS Bedrock** *(AI Chat/Embed)* | `AmazonServiceException`, `AmazonClientException` | - `ThrottlingException` (429)<br>- `InternalServerException` (500)<br>- `ServiceUnavailableException` (503)<br>- `APICallRateExceededException` | - `AccessDeniedException` (403)<br>- `ValidationException` (400)<br>- `ModelNotReadyException` | **Exponential Backoff + Jitter**<br>- Base Delay: `1s`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **Azure OpenAI** *(AI Chat/Embed)* | **`System.ClientModel.ClientResultException`** | - Status Code `429` (Too Many Requests)<br>- Status Code `500` (Internal Error)<br>- Status Code `502/503/504` (Gateway/Service Unavailable) | - Status Code `400` (Bad Request/Validation)<br>- Status Code `401/403` (Unauthorized/Access Denied) | **Exponential Backoff + Jitter**<br>- Base Delay: `1s`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **AWS OpenSearch** *(Vector Store)* | `OpenSearchClientException` *(native)* | - Status Code `429` (Request Rate Limit)<br>- Status Code `502/503/504` (Gateway/Service Unavailable)<br>- TCP/Socket connection exceptions | - Status Code `400` (Index or Query error)<br>- Status Code `401/403` (SigV4 Auth / Access Denied) | **Exponential Backoff + Jitter**<br>- Base Delay: `800ms`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **Azure AI Search** *(Vector Store)* | `Azure.RequestFailedException` | - Status Code `429` (Request Throttled)<br>- Status Code `503/504` (Unavailable/Timeout) | - Status Code `400` (Validation)<br>- Status Code `401/403` (Key Auth) | **Exponential Backoff + Jitter**<br>- Base Delay: `800ms`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **DynamoDB Repositories** *(DB/Persistence)* | `AmazonServiceException`, `AmazonClientException` | - `ProvisionedThroughputExceededException` (429)<br>- `InternalServerError` (500)<br>- `LimitExceededException` | - `ResourceNotFoundException` (404)<br>- `ConditionalCheckFailedException`<br>- `ValidationException` (400) | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `4`<br>- Scale Factor: `2.0` |
| **Cosmos DB Repositories** *(DB/Persistence)* | `Microsoft.Azure.Cosmos.CosmosException` | - Status Code `429` (Request Rate Too Large)<br>- Status Code `503/504` (Unavailable/Timeout)<br>- Status Code `408` (Request Timeout) | - Status Code `404` (Not Found)<br>- Status Code `409` (Conflict)<br>- Status Code `400/401/403` (Auth/Validation) | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `4`<br>- Scale Factor: `2.0` |
| **AWS Cognito** *(User Management)* | `AmazonServiceException`, `AmazonClientException` | - `TooManyRequestsException` (429)<br>- `InternalErrorException` (500) | - `UserNotFoundException`<br>- `NotAuthorizedException`<br>- `InvalidParameterException` | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Microsoft Graph** *(User Management)* | **`Microsoft.Graph.Models.ODataErrors.ODataError`** | - Status Code `429` (Too Many Requests)<br>- Status Code `503/504` (Service Unavailable)<br>- Transient TCP timeout exceptions | - Status Code `400/401/403/404` | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Redis Cache** *(Caching)* | `StackExchange.Redis.RedisConnectionException`, `RedisTimeoutException` | - Socket / Connection timeouts<br>- Connection multiplexer drops | - Syntax/command exceptions | **Linear Backoff**<br>- Base Delay: `100ms`<br>- Retries: `2` |
| **S3 Storage** *(Storage)* | `AmazonS3Exception` | - Status Code `500/503/504` | - Status Code `404` (Bucket/Key Not Found)<br>- Status Code `403` (Access Denied) | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Blob Storage** *(Storage)* | `Azure.RequestFailedException` | - Status Code `500/503/504` | - Status Code `404` (Container/Blob Not Found)<br>- Status Code `403` (Access Denied) | **Exponential Backoff + Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Document Processing** *(OCR/Textract)* | `AmazonServiceException`, `Azure.RequestFailedException` | - Throttling/Throughput Exceeded exceptions<br>- Status Code `500/503/504` | - `UnsupportedDocumentException`<br>- `InvalidParameterException` | **Exponential Backoff + Jitter**<br>- Base Delay: `2s`<br>- Retries: `3` |

---

## Updated Circuit Breaker Matrix

Circuit breakers isolate persistent downstream failures. When a downstream service is degraded, the circuit "opens," failing fast instantly to protect system resources (such as thread pools and CPU cycles) and allowing the downstream service time to recover.

### Database Repository Exclusions
As determined during the codebase review, **database repositories (DynamoDB and Cosmos DB) are excluded from circuit breaker policies**. Because databases are critical state repositories with no secondary fallback, failing fast via a circuit breaker creates false-alarm outages. Databases will rely strictly on **retries** and **timeouts**.

### Registered Circuit Breakers

| Circuit Target | Trigger Threshold | Sampling Window | Break Duration | Recovery Criteria |
| :--- | :--- | :--- | :--- | :--- |
| **AWS Bedrock Chat / Embed** | `Failure Ratio >= 50%` | `30 seconds` (min. 10 requests) | `15 seconds` | `5` consecutive successes in `Half-Open` state |
| **Azure OpenAI Chat / Embed** | `Failure Ratio >= 50%` | `30 seconds` (min. 10 requests) | `15 seconds` | `5` consecutive successes in `Half-Open` state |
| **AWS OpenSearch Client** | `Failure Ratio >= 40%` | `30 seconds` (min. 10 requests) | `20 seconds` | `3` consecutive successes in `Half-Open` state |
| **Azure AI Search Client** | `Failure Ratio >= 40%` | `30 seconds` (min. 10 requests) | `20 seconds` | `3` consecutive successes in `Half-Open` state |
| **Cognito User Service** | `Failure Ratio >= 50%` | `30 seconds` (min. 10 requests) | `15 seconds` | `3` consecutive successes in `Half-Open` state |
| **Microsoft Graph User Service** | `Failure Ratio >= 50%` | `30 seconds` (min. 10 requests) | `15 seconds` | `3` consecutive successes in `Half-Open` state |
| **Redis Cache connection** | `Failure Ratio >= 40%` | `15 seconds` (min. 10 requests) | `30 seconds` | `5` consecutive successes in `Half-Open` state |
| **Textract / Document Intelligence** | `Failure Ratio >= 50%` | `60 seconds` (min. 5 requests) | `60 seconds` | `2` consecutive successes in `Half-Open` state |

---

## SDK Retry Interaction Strategy

To prevent retry amplification (the multiplying of retry attempts between Polly and the underlying cloud SDKs, which exacerbates throttling), we will align the native SDK settings with Polly.

```
Request ──> [Polly Pipeline (Timeout Guard)] ──> [Polly Retry] ──> [SDK Client (MaxRetries = 0 or 1)] ──> Network
```

### Coexistence Rules

1. **AWS SDK v4**:
   - **Strategy**: By default, AWS SDK v4 has automatic retries enabled (usually 3). We will configure `AmazonRequestQueueConfig` or individual `ClientConfig` settings in the dependency injection configuration to set `MaxErrorRetry = 1` or `RetryMode = RequestRetryMode.Legacy` (with `MaxErrorRetry = 0`). 
   - **Reasoning**: This delegates retry scheduling, exponential backoff, jitter, and observability directly to Polly, preventing the AWS client from performing nested retries inside Polly’s attempts.

2. **Cosmos DB SDK**:
   - **Strategy**: Keep the Cosmos DB SDK's native rate-limit retry policy active (e.g. `CosmosClientOptions.MaxRetryAttemptsOnThrottledRequests = 9`), but configure Polly's Cosmos policy to catch only exceptions that escape this built-in SDK layer.
   - **Reasoning**: The Cosmos SDK handles `429` rate limiting highly efficiently at the request/partition layer using direct backend telemetry. Polly will act as a second-level safety net for non-throttled transient errors (such as HTTP `503` or timeouts) and database-level timeouts.

3. **Azure SDKs (Storage and AI Search)**:
   - **Strategy**: Configure `BlobClientOptions.Retry` and `SearchClientOptions.Retry` to set `MaxRetries = 0` or `MaxRetries = 1` during client registration.
   - **Reasoning**: This disables the built-in `Azure.Core` retry policies, allowing Polly to execute, log, and measure all retry attempts.

4. **Microsoft Graph SDK**:
   - **Strategy**: Retain the Graph client's native `RetryHandler` middleware, but configure Polly to execute with `MaxRetryAttempts = 1` or `2` for non-429 exceptions.
   - **Reasoning**: The Graph middleware reads the `Retry-After` header directly to respect rate limits. Polly will handle non-429 transient exceptions and enforce strict external timeouts.

---

## Production-Grade OpenSearch Exception Strategy

### Current Issue
Currently, `OpenSearchService` catches internal client failures and translates them into a generic `InvalidOperationException`:
```csharp
if (!response.Success)
    throw new InvalidOperationException($"Vector search kNN query failed: {response.DebugInformation}");
```
This masks the original transient error types (e.g. rate limits or connection drops), making it impossible for Polly's exception predicates to distinguish transient faults from bugs.

### Proposed Strategy (Recommended)
Instead of custom exception wrapping or parsing exception messages, we will configure the underlying connection pool to **propagate native exceptions**:

1. **Enable Native Exceptions**: Configure `ConnectionSettings` in `OpenSearchService` constructor to throw native exceptions:
   ```csharp
   var settings = new ConnectionSettings(pool, awsConnection)
       .ThrowExceptions(true) // Configures the SDK to throw OpenSearchClientException natively
       .DefaultIndex(_indexName);
   ```
2. **Polly Integration**: Polly's retry policy will handle `OpenSearchClientException` (and transient connection exceptions like `SocketException` or `IOException`) directly.
3. **Benefits**:
   - Prevents exception swallowing / wrapping.
   - Preserves complete stack traces and native HTTP status codes.
   - Avoids fragile string parsing of `DebugInformation` or exception messages.

---

## Updated Files Expected To Change

Resilience registration will occur dynamically in the Infrastructure and Ingestion layers, keeping the core Application and Domain layers clean.

### Core Projects
* **[`AwsRagChat.Infrastructure.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj) [MODIFY]**:
  - Add `Polly` (v8.3.0) and `Microsoft.Extensions.Http.Polly` (v8.0.0) package references.
* **[NEW] `AwsRagChat.Infrastructure/Resilience/ResiliencePipelines.cs`**:
  - Central registry defining Polly v8 pipelines (Retry, Circuit Breaker, Timeout strategies) grouped by service name.
  - Expose extension methods to run operations under specific pipelines.

### AWS Infrastructure Layer
* **[`AwsProviderServiceCollectionExtensions.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs) [MODIFY]**:
  - Register AWS-specific resilience pipelines in DI.
  - Configure `ClientConfig` on AWS SDK clients to set `MaxErrorRetry = 1` or `0` to prevent retry amplification.
* **[`BedrockChatCompletionService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs) [MODIFY]**:
  - Wrap `InvokeNovaAsync` calls in the Bedrock resilience pipeline.
* **[`BedrockEmbeddingService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs) [MODIFY]**:
  - Wrap `InvokeModelAsync` embedding calls.
* **[`OpenSearchService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/OpenSearchService.cs) [MODIFY]**:
  - Configure `ThrowExceptions(true)` in the constructor.
  - Wrap search and indexing operations in the OpenSearch resilience pipeline.
* **DynamoDB Repositories [MODIFY]**:
  - Wrap database operations in DynamoDB retry/timeout strategies (no circuit breakers):
    - [`DynamoDbChunkRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs)
    - [`DynamoDbConversationRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbConversationRepository.cs)
    - [`DynamoDbDocumentRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs)
    - [`DynamoDbUserRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbUserRepository.cs) *(Expanded coverage)*
* **[`CognitoUserRoleService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/CognitoUserRoleService.cs) [MODIFY]**:
  - Wrap Cognito Admin group lists/add calls in the Cognito resilience pipeline.

### Azure Infrastructure Layer
* **[`AzureProviderServiceCollectionExtensions.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/DependencyInjection/AzureProviderServiceCollectionExtensions.cs) [MODIFY]**:
  - Register Azure-specific resilience pipelines in DI.
  - Configure Azure client options to disable default SDK retries where Polly handles it.
* **[`AzureOpenAiChatService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs) [MODIFY]**:
  - Wrap `CompleteChatAsync` in OpenAI resilience pipelines catching `System.ClientModel.ClientResultException`.
* **[`AzureOpenAiEmbeddingService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/AzureOpenAiEmbeddingService.cs) [MODIFY]**:
  - Wrap embedding client operations.
* **[`AzureAiSearchVectorStore.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/AzureAiSearchVectorStore.cs) [MODIFY]**:
  - Wrap vector search index and lookup operations in AI Search resilience pipeline.
* **Cosmos DB Repositories [MODIFY]**:
  - Wrap database operations in Cosmos DB retry/timeout strategies (no circuit breakers):
    - [`CosmosDbChunkRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbChunkRepository.cs)
    - [`CosmosDbConversationRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbConversationRepository.cs)
    - [`CosmosDbDocumentRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbDocumentRepository.cs)
    - [`CosmosDbUserRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbUserRepository.cs) *(Expanded coverage)*
    - [`CosmosDbDocumentStatusService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbDocumentStatusService.cs) *(Expanded coverage)*
* **[`EntraUserRoleService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/EntraUserRoleService.cs) [MODIFY]**:
  - Wrap Microsoft Graph Client operations catching `Microsoft.Graph.Models.ODataErrors.ODataError`.

### Caching Layer
* **[`RedisCacheService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Cache/RedisCacheService.cs) [MODIFY]**:
  - Intercept Redis connection/timeout exceptions inside `GetAsync`, `SetAsync`, and `RemoveAsync` using a Polly fallback policy, returning `default` or bypassing.

### Ingestion Layer
* **[`AwsRagChat.Ingestion.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj) [MODIFY]**:
  - Add `Polly` package reference.
* **[`AzureDocumentProcessor.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/Azure/AzureDocumentProcessor.cs) [MODIFY]**:
  - Wrap DocumentIntelligenceClient calls in retry/circuit breaker pipelines.
* **[`AwsDocumentProcessor.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/Aws/AwsDocumentProcessor.cs) [MODIFY]**:
  - Wrap Textract synchronous/asynchronous client operations.

---

## Risks

| Risk | Severity | Mitigation |
| :--- | :--- | :--- |
| **Nested Retry Amplification** | High | Set `MaxErrorRetry = 1` or `0` on AWS SDK clients and `MaxRetries = 0` on Azure core-based clients (Storage, AI Search). Maintain Cosmos DB rate-limit retries but only trigger Polly for failures escaping that layer. |
| **Thread Pool Starvation** | Medium | Use strictly non-blocking async hooks (`Polly.Async` / `AddRetryAsync` / `AddTimeoutAsync`) throughout the pipeline. Ensure no synchronous blocking calls (`.Wait()` or `.Result`) exist. |
| **Cascading Latencies (Timeout Accumulation)** | High | Restrict total retry attempts to 3 or 4 with jittered delays, and implement parent-level timeouts that cancel task tokens immediately. |
| **Cosmos DB RU Starvation** | Medium | Let the Cosmos DB SDK handle 429 throttling natively via its options first. Only layer Polly on top for cross-resource stability or non-429 exceptions. |
| **Swallowed OpenSearch Errors** | Low | By configuring `ThrowExceptions(true)` on `ConnectionSettings`, OpenSearch client errors will propagate directly, allowing Polly to catch the true exception types. |

---

## Validation Plan

### 1. Build Validation
Ensure the solution builds cleanly across all projects:
```powershell
dotnet build AwsRagChat\AwsRagChat.slnx
```

### 2. Integration and Simulation Testing
- **Mock Client Injections**: Use Mocked handlers simulating HTTP `429 (Too Many Requests)` or `503 (Service Unavailable)`. Verify that the service executes the exact number of retry attempts configured.
- **Circuit Breaker State Verifications**: Simulate repeated failures to verify the circuit state transitions to `Open` and rejects subsequent calls with `BrokenCircuitException`. Verify recovery behavior after the cooldown window.
- **Timeout Verifications**: Simulate delayed responses and verify that operations throw `TimeoutRejectedException` / `OperationCanceledException` at the exact timeout boundary.

---

## Definition Of Done

- [ ] Polly package integrated across infrastructure and ingestion projects.
- [ ] Central registry `ResiliencePipelines` defined for AWS, Azure, Storage, and Cache operations.
- [ ] Every transient external call (AI, Search, Databases, Auth, Storage) wrapped in a structured resilience policy.
- [ ] Database repositories (Cosmos DB and DynamoDB) have retry and timeout policies applied but are excluded from circuit breakers.
- [ ] OpenSearch client is configured to throw native exceptions, and Polly is configured to catch `OpenSearchClientException`.
- [ ] AWS and Azure client SDKs have their native retry limits configured to prevent retry amplification.
- [ ] Zero blocking synchronous wraps used in pipelines.
- [ ] Structured logging statement hooks registered for every retry, break, and reset event.
- [ ] Build succeeds with `0 warnings` and `0 errors`.
