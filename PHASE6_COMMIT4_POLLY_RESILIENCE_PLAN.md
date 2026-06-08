# PHASE6_COMMIT4_POLLY_RESILIENCE_PLAN

## Executive Summary

To ensure high availability and self-healing capabilities in production, this plan introduces a centralized, production-grade resilience strategy using **Polly v8** (the high-performance, allocation-free .NET resilience library).

The platform communicates with numerous external dependencies (SaaS, IaaS, and PaaS endpoints) over the network. Under load, these dependencies can fail transiently due to network glitches, rate-limiting (throttling), or temporary server-side degradation.

This plan outlines the integration of standard, structured Polly `ResiliencePipelines` directly into our cloud infrastructure and ingestion layers. It covers retry classification, circuit breakers, timeouts, and structured logging without modifying core Application or Domain layers.

---

## Retry Classification Matrix

Transient errors are retried using exponential backoff with jitter to prevent herd effects (thundering herd problem). Non-transient errors (such as authentication failures or validation errors) fail fast immediately.

| Dependency / Service | Retryable Failures (Retried) | Non-Retryable Failures (Fast Fail) | Backoff & Jitter Strategy |
| :--- | :--- | :--- | :--- |
| **AWS Bedrock** *(BedrockChatCompletionService, BedrockEmbeddingService)* | - `ThrottlingException` (HTTP 429)<br>- `InternalServerException` (HTTP 500)<br>- `ServiceUnavailableException` (HTTP 503)<br>- `APICallRateExceededException` | - `AccessDeniedException` (HTTP 403)<br>- `ValidationException` (HTTP 400)<br>- `ModelNotReadyException` | **Exponential Backoff with Jitter**<br>- Base Delay: `1s`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **Azure OpenAI** *(AzureOpenAiChatService, AzureOpenAiEmbeddingService)* | - `ClientResultException` with Status 429<br>- `ClientResultException` with Status 500/502/503/504 | - `ClientResultException` with Status 400 (Bad Prompt)<br>- `ClientResultException` with Status 401/403 (Auth/API Key) | **Exponential Backoff with Jitter**<br>- Base Delay: `1s`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **AWS OpenSearch** *(OpenSearchService)* | - `OpenSearchClientException` with Status 429/502/503/504<br>- Socket / Timeout TCP exceptions | - `OpenSearchClientException` with Status 400 (Bad query)<br>- `OpenSearchClientException` with Status 401/403 (IAM/SigV4) | **Exponential Backoff with Jitter**<br>- Base Delay: `800ms`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **Azure AI Search** *(AzureAiSearchVectorStore)* | - `RequestFailedException` with Status 429/503/504<br>- Transient socket errors | - `RequestFailedException` with Status 400 / 401 / 403 | **Exponential Backoff with Jitter**<br>- Base Delay: `800ms`<br>- Retries: `3`<br>- Scale Factor: `2.0` |
| **DynamoDB Repositories** *(DynamoDbChunkRepository, etc.)* | - `ProvisionedThroughputExceededException`<br>- `InternalServerError` (HTTP 500)<br>- `LimitExceededException` | - `ResourceNotFoundException`<br>- `ConditionalCheckFailedException`<br>- `ValidationException` | **Exponential Backoff with Jitter**<br>- Base Delay: `500ms`<br>- Retries: `4`<br>- Scale Factor: `2.0` |
| **Cosmos DB Repositories** *(CosmosDbChunkRepository, etc.)* | - `CosmosException` with Status 429 (Request Rate Too Large)<br>- `CosmosException` with Status 503 / 504 / 408 | - `CosmosException` with Status 404 (Not Found)<br>- `CosmosException` with Status 409 (Conflict)<br>- `CosmosException` with Status 400 / 401 / 403 | **Exponential Backoff with Jitter**<br>- Base Delay: `500ms`<br>- Retries: `4`<br>- Scale Factor: `2.0` |
| **AWS Cognito** *(CognitoUserRoleService)* | - `TooManyRequestsException` (HTTP 429)<br>- `InternalErrorException` (HTTP 500) | - `UserNotFoundException`<br>- `NotAuthorizedException`<br>- `InvalidParameterException` | **Exponential Backoff with Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Microsoft Graph** *(EntraUserRoleService)* | - `ODataError` with Status 429 / 503 / 504<br>- Transient HTTP timeout exceptions | - `ODataError` with Status 400 / 401 / 403 / 404 | **Exponential Backoff with Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |
| **Redis Cache** *(RedisCacheService)* | - `RedisConnectionException`<br>- `RedisTimeoutException` | - `RedisCommandException` (syntax errors) | **Linear Backoff** (Fast retry)<br>- Base Delay: `100ms`<br>- Retries: `2` |
| **Document Processing** *(Textract / Document Intelligence)* | - `RequestFailedException` with Status 429/500/503/504<br>- `ProvisionedThroughputExceededException` | - `UnsupportedDocumentException`<br>- `InvalidParameterException`<br>- Auth / Key exceptions | **Exponential Backoff with Jitter**<br>- Base Delay: `2s`<br>- Retries: `3` (higher base delay due to heavy payload extraction) |
| **Storage (S3 / Blob Storage)** | - `AmazonS3Exception` (HTTP 500/503/504)<br>- `RequestFailedException` (HTTP 500/503/504) | - `NoSuchBucket` / `BlobNotFound` (HTTP 404)<br>- Access Denied (HTTP 403) | **Exponential Backoff with Jitter**<br>- Base Delay: `500ms`<br>- Retries: `3` |

---

## Circuit Breaker Strategy

Circuit breakers isolate persistent downstream failures. When a downstream service is degraded, the circuit "opens," failing fast instantly to protect local system resources (such as thread pools and CPU cycles) and allowing the downstream service time to recover.

### Circuit Breaker Specifications

1. **AI Generation Services (Bedrock / Azure OpenAI)**:
   - **Scope**: Applied per API Client (Chat / Embedding separately).
   - **Trigger Threshold**: `Failure Ratio >= 50%` over a `30s` sampling window, with a minimum of `10` requests.
   - **Break Duration**: `15s` of complete silence (fast-fail on all incoming requests).
   - **Recovery**: `5` consecutive successful requests in a `Half-Open` state closes the circuit.
   
2. **Persistence Repositories (DynamoDB / Cosmos DB)**:
   - **Scope**: Applied at the repository class level.
   - **Trigger Threshold**: `Failure Ratio >= 30%` over a `30s` sampling window, with a minimum of `15` requests.
   - **Break Duration**: `10s` (short break because databases must recover quickly or failover).
   - **Recovery**: `5` consecutive successful operations in a `Half-Open` state.

3. **Redis Caching**:
   - **Scope**: Applied globally to the cache connection wrapper.
   - **Trigger Threshold**: `Failure Ratio >= 40%` over a `15s` sampling window, with a minimum of `10` requests.
   - **Break Duration**: `30s` (longer break to allow Redis host reconnection).
   - **Recovery**: Fail-safe fallback is active during the break (cache bypasses directly to database).

4. **Document Ingestion Services (Textract / Document Intelligence)**:
   - **Scope**: Applied within background processors.
   - **Trigger Threshold**: `Failure Ratio >= 50%` over a `60s` sampling window, with a minimum of `5` runs.
   - **Break Duration**: `60s` (longer break as ingestion runs asynchronously).
   - **Recovery**: `2` consecutive successes in `Half-Open` state.

---

## Timeout Strategy

To prevent hanging threads and resource leaks, we implement **Optimistic timeouts** on all external network boundaries. Every network operation must accept a `CancellationToken` and map to a strict timeout policy.

### Timeout Specifications

| Service / Endpoint | Timeout Duration | Policy Type | Action on Timeout |
| :--- | :--- | :--- | :--- |
| **Bedrock Chat / OpenAI Chat** | `45 seconds` | Optimistic (`CancellationToken` based) | Abort request, cancel HTTP stream, propagate `OperationCanceledException` |
| **Bedrock Embed / OpenAI Embed** | `15 seconds` | Optimistic | Cancel embedding request |
| **OpenSearch / Azure AI Search** | `10 seconds` | Optimistic | Abort vector lookup query |
| **DynamoDB / Cosmos DB** | `5 seconds` | Optimistic | Abort read/write persistence query |
| **Cognito / Microsoft Graph** | `5 seconds` | Optimistic | Fail role lookup quickly |
| **Redis Cache operations** | `1 second` | Pessimistic / Optimistic | Fallback to direct DB read/write |
| **S3 / Blob Storage Upload** | `30 seconds` | Optimistic | Cancel binary upload |
| **Document Processing (Sync)** | `60 seconds` | Optimistic | Abort synchronous OCR/Text extraction |

---

## Logging & Telemetry Requirements

Resilience events must be fully observable to distinguish transient network noise from systemic failures.

### 1. Structured Logging (`ILogger`)
All Polly event hooks must write structured logging statements to stdout:
* **On Retry**:
  ```
  [Retry Attempt {AttemptNumber} of {MaxAttempts}] Transient exception {ExceptionType} encountered for service {ServiceName}. Delaying {Delay} before next attempt.
  ```
* **On Circuit Break**:
  ```
  [CRITICAL] Circuit breaker for {ServiceName} has OPENED. downstream is degraded. Duration: {BreakDuration} seconds. Exception: {ExceptionMessage}
  ```
* **On Circuit Reset**:
  ```
  [INFO] Circuit breaker for {ServiceName} has CLOSED. Downstream is healthy.
  ```

### 2. Custom Metrics (OpenTelemetry/Application Insights)
We will expose custom Metrics counters to tracking resilience behavior:
- `resilience.retry.count` (dimensions: `service_name`, `attempt_number`, `exception_type`)
- `resilience.circuit_breaker.state` (dimensions: `service_name`, `state` [Open, Closed, Half-Open])
- `resilience.timeout.count` (dimensions: `service_name`)

---

## Files Expected To Change

We will implement this by centralizing pipeline definitions in the Infrastructure layer and registering them via DI, keeping the Application layer completely clean.

### Core Projects

#### [`AwsRagChat.Infrastructure.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj) [MODIFY]
- Add package reference:
  ```xml
  <PackageReference Include="Polly" Version="8.3.0" />
  <PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.0.0" />
  ```

#### [NEW] [`ResiliencePipelines.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Resilience/ResiliencePipelines.cs)
- Create a central registry for all Polly v8 pipelines (Retry, Circuit Breaker, Timeout strategies) grouped by service name.
- Provide helper extensions to easily execute functions under these pipelines.

---

### AWS Provider Implementations

#### [`AwsProviderServiceCollectionExtensions.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Aws/AwsProviderServiceCollectionExtensions.cs) [MODIFY]
- Register AWS-specific resilience pipelines into the service collection.

#### [`BedrockChatCompletionService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs) [MODIFY]
- Wrap `InvokeNovaAsync` using the Bedrock retry/timeout pipeline.

#### [`BedrockEmbeddingService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/BedrockEmbeddingService.cs) [MODIFY]
- Wrap `InvokeModelAsync` embedding calls.

#### [`OpenSearchService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/OpenSearchService.cs) [MODIFY]
- Wrap search and index requests.

#### [`DynamoDbChunkRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs) [MODIFY]
- Wrap BatchWrite and Get operations.

#### [`DynamoDbConversationRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbConversationRepository.cs) [MODIFY]
- Wrap CRUD operations.

#### [`DynamoDbDocumentRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs) [MODIFY]
- Wrap DynamoDB calls.

#### [`CognitoUserRoleService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/CognitoUserRoleService.cs) [MODIFY]
- Wrap Admin Cognito operations.

---

### Azure Provider Implementations

#### [`AzureProviderServiceCollectionExtensions.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/DependencyInjection/AzureProviderServiceCollectionExtensions.cs) [MODIFY]
- Register Azure-specific resilience pipelines into the service collection.

#### [`AzureOpenAiChatService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs) [MODIFY]
- Wrap OpenAI chat completion invocations.

#### [`AzureOpenAiEmbeddingService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/AzureOpenAiEmbeddingService.cs) [MODIFY]
- Wrap OpenAI embedding calls.

#### [`CosmosDbChunkRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbChunkRepository.cs) [MODIFY]
- Wrap Cosmos DB query, upsert, and delete SDK operations.

#### [`CosmosDbConversationRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbConversationRepository.cs) [MODIFY]
- Wrap conversation persistence operations.

#### [`CosmosDbDocumentRepository.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/CosmosDbDocumentRepository.cs) [MODIFY]
- Wrap document persistence operations.

#### [`EntraUserRoleService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/EntraUserRoleService.cs) [MODIFY]
- Wrap Microsoft Graph SDK queries.

---

### Ingestion Projects

#### [`AwsRagChat.Ingestion.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj) [MODIFY]
- Reference `Polly` package.

#### [`AzureDocumentProcessor.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/Azure/AzureDocumentProcessor.cs) [MODIFY]
- Wrap DocumentIntelligence SDK operations in a resilience strategy.

#### [`AwsDocumentProcessor.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/Aws/AwsDocumentProcessor.cs) [MODIFY]
- Wrap Textract synchronous/asynchronous operations.

---

### Shared Caching

#### [`RedisCacheService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Cache/RedisCacheService.cs) [MODIFY]
- Wrap `IDistributedCache` calls in a fail-safe fallback retry structure. If Redis throws timeout/connection exceptions, log and gracefully return default/null to hit the database fallback.

---

## Risks

| Risk | Severity | Mitigation |
| :--- | :--- | :--- |
| **Thread Pool Starvation** | Medium | Use strictly non-blocking async hooks (`Polly.Async` / `AddRetryAsync` / `AddTimeoutAsync`) throughout the pipeline. Ensure no synchronous blocking calls (`.Wait()` or `.Result`) exist. |
| **Cascading Failures (Latency Amplification)** | High | Under high load, nested retries can cascade timeouts upstream. Mitigation: Strictly bound the maximum retries to `3` or `4` with a maximum cumulative backoff cap of `5 seconds`, and implement parent-level timeouts. |
| **Cache Stampede / DB Overload during Outage** | Medium | Apply Jitter (`UseJitter = true` on Polly options) to spread out retries over time. Ensure the Circuit Breaker break duration is long enough (`15s`+) to let target databases recover. |
| **Authentication Failures Blocked by Polly** | Low | Ensure all auth-related exceptions (`401`, `403`, signature mismatch) are strictly classified as non-retryable in `ShouldHandle` predicates. |
| **Cosmos DB RU Starvation** | Medium | Configure the Cosmos DB SDK's native `MaxRetryAttemptsOnThrottledRequests` first, then layer Polly on top for cross-resource stability. |

---

## Validation Plan

### 1. Build Validation
Ensure the solution builds cleanly across all projects:
```powershell
dotnet build AwsRagChat\AwsRagChat.slnx
```

### 2. Unit and Integration Testing
We will build integration test harnesses simulating transient failures:
- **Mock Client Injections**: Use Mocked handlers simulating HTTP `429 (Too Many Requests)` or `503 (Service Unavailable)`. Verify that the service executes the exact number of retry attempts configured.
- **Circuit Breaker State Verifications**: Simulate repeated failures to verify the circuit state transitions to `Open` and rejects subsequent calls with `BrokenCircuitException`. Verify recovery behavior after the cooldown window.
- **Timeout Verifications**: Simulate delayed responses and verify that operations throw `TimeoutRejectedException` / `OperationCanceledException` at the exact timeout boundary.

---

## Definition Of Done

- [ ] Polly NuGet package integrated across infrastructure and ingestion projects.
- [ ] Central registry `ResiliencePipelines` defined for AWS, Azure, Storage, and Cache operations.
- [ ] Every transient external call (AI, Search, Databases, Auth, Storage) wrapped in a structured resilience policy.
- [ ] Zero blocking synchronous wraps used in pipelines.
- [ ] Structured logging statement hooks registered for every retry, break, and reset event.
- [ ] Unit and integration tests verify retry counts, timeout boundaries, and circuit breaker behavior.
- [ ] Clean build execution (`0 warnings`, `0 errors`) on the solution.
