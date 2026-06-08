# Phase 6 Plan: Validation & Production Readiness Plan

This document outlines the validation, security, resilience, and operational engineering tasks required to transition the completed multi-cloud RAG platform (AWS/Azure) into a production-ready system.

---

# Executive Summary

With the successful completion of Phase 5 (Commits 1–7), the core multi-cloud capabilities of the RAG platform are fully implemented and compile cleanly:
*   **Abstraction Parity:** Provider-neutral interfaces exist for storage, embeddings, chat completions, vector databases, document OCR/parsing, and database repositories.
*   **Dynamic Cloud Switcher:** The system dynamically routes infrastructure registrations and JWT validation schemes between AWS and Azure based on the `"CloudProvider"` configuration key.

However, validation of the dynamic configuration paths identified critical gaps that prevent production deployment:
1.  **Startup DI Blockers:** In Azure mode, controller dependencies on AWS-specific SDK clients (`IAmazonCognitoIdentityProvider`) throw execution errors during API startup.
2.  **Missing Azure Event Orchestration:** The Azure ingestion pipeline lacks an event-driven worker trigger (Azure Blob/Queue function) to automatically run chunking, embedding, and indexing when files are uploaded to Azure storage.
3.  **Authentication and Configuration Gaps:** Claim translation rules differ in structure, plain-text secrets reside in appsettings configuration, and transient network errors (e.g. HTTP 429) can crash queries or ingestion.

Phase 6 addresses these gaps via a sequence of reviewable, decoupled commits.

---

# Recommended Phase Structure

Each commit is designed to be isolated, testable, and focused on resolving a specific readiness area without introducing regressions into the neutral domain layers.

1.  **Commit 1: Cloud-Neutral API Authentication & Controller Refactoring (P0/P1)**
    *   *Rationale:* Eliminates the DI dependency blocker on `IAmazonCognitoIdentityProvider` in Azure mode and unifies role claim evaluation across both Cognito and Entra ID configurations.
2.  **Commit 2: Azure Ingestion Trigger/Worker Implementation (P0)**
    *   *Rationale:* Implements the event trigger endpoint (e.g., Azure Storage Blob Trigger or Event Grid Queue Trigger) to automatically invoke the Azure document ingestion pipeline.
3.  **Commit 3: Production-Grade Managed Secrets Management (P1)**
    *   *Rationale:* Integrates AWS Secrets Manager and Azure Key Vault into the configuration builder, ensuring no plaintext credentials reside in the deployment repositories.
4.  **Commit 4: Polly Resilience and Retry Policies (P1)**
    *   *Rationale:* Implements error handling, retries, and back-offs for Azure OpenAI, Bedrock, Cosmos DB, DynamoDB, OpenSearch, and Azure AI Search APIs.
5.  **Commit 5: Unified Observability and Structured Telemetry (P2)**
    *   *Rationale:* Configures OpenTelemetry distributed tracing and structured JSON logging to monitor cross-cloud latency and trace RAG execution.
6.  **Commit 6: Infrastructure as Code (IaC) templates (P2)**
    *   *Rationale:* Standardizes deployment via Terraform, Bicep, or CloudFormation templates to eliminate manual resource creation.

---

# Commit Roadmap

## Phase 6 Commit 1: Cloud-Neutral API Authentication & Controller Refactoring

### Goal
Decouple `AuthController` from the Amazon Cognito SDK client and standardize role claim parsing in controllers.

### Why It Is Needed
Currently, `AuthController` injects `IAmazonCognitoIdentityProvider` in its constructor, causing startup crashes when running in Azure mode. Additionally, `UserRoleController` reads roles by checking static `"cognito:groups"` or raw `"groups"` claim types directly, failing to recognize normalized standard roles assigned to the `ClaimTypes.Role` collection.

### Priority
*   **P0** (DI Blocker) and **P1** (Role Parsing)

### Files Expected To Change
*   `AwsRagChat.Api/Controllers/AuthController.cs`
*   `AwsRagChat.Api/Controllers/UserRoleController.cs`
*   `AwsRagChat.Infrastructure/Aws/AwsAuthenticationExtensions.cs`
*   `AwsRagChat.Infrastructure/Azure/EntraAuthenticationExtensions.cs`

### Dependencies
*   None.

### Risks
*   A user registering in AWS mode could encounter Cognito authentication failures if Cognito client instantiation behaves incorrectly at runtime.

### Validation Approach
1.  **Azure Mode DI Verification:** Set `CloudProvider = "Azure"` and verify that the API starts and resolves controllers successfully without throwing `DependencyResolutionException`.
2.  **AWS Register Testing:** Run user registration on AWS Cognito and verify that `/api/auth/register` works normally.
3.  **Azure Register Mocking:** Ensure that hitting `/api/auth/register` in Azure mode returns a clear `400 Bad Request` or `501 Not Implemented` indicating that user registration is handled directly via Microsoft Entra ID.
4.  **Role Verification:** Verify that `/api/userrole` successfully returns normalized role mappings under both Cognito and Entra configurations.

### Definition Of Done
*   `AuthController` compiles and executes without requiring Cognito clients in the constructor.
*   User roles are resolved cleanly from standard claims (`ClaimTypes.Role`) in both provider paths.

---

## Phase 6 Commit 2: Azure Ingestion Trigger/Worker Implementation

### Goal
Implement an event-driven function trigger inside the Ingestion layer to execute Azure-specific document ingestion.

### Why It Is Needed
While `AzureIngestionComposition.cs` compiles, the `AwsRagChat.Ingestion` project contains only AWS Lambda handlers. There is no background worker or Azure Function trigger configured to start ingestion when a file is uploaded to Azure Blob Storage.

### Priority
*   **P0**

### Files Expected To Change
*   `AwsRagChat.Ingestion/Handlers/AzureBlobIngestionFunction.cs` [NEW]
*   `AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj`
*   `AwsRagChat.Ingestion/Azure/AzureIngestionComposition.cs` (slight wiring update if needed)

### Dependencies
*   `Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs` or equivalent Azure trigger SDKs.

### Risks
*   Concurrent uploads could cause race conditions or duplicate processing in Cosmos DB if partition key structures are misaligned.
*   Cold start delays in Serverless Azure Functions could delay ingestion completions.

### Validation Approach
1.  **Local Emulation:** Run Azure Functions Core Tools locally using Azurite.
2.  **E2E Azure Ingestion Execution:** Upload a document via `DocumentsController.Upload` in Azure mode. Verify that the Azure blob trigger starts the extraction pipeline, generates embeddings using Azure OpenAI, and updates Cosmos DB and Azure AI Search.

### Definition Of Done
*   Uploading a PDF to the Azure Storage Container automatically triggers the ingestion pipeline, resulting in the file status moving from `UPLOADED` to `INDEXED`.

---

## Phase 6 Commit 3: Production-Grade Managed Secrets Management

### Goal
Integrate AWS Secrets Manager and Azure Key Vault directly into the application's configuration pipeline.

### Why It Is Needed
Production security standards require that access keys, API keys, database connection strings, and authorization secrets be loaded securely at runtime rather than residing in plaintext configurations.

### Priority
*   **P1**

### Files Expected To Change
*   `AwsRagChat.Api/Program.cs`
*   `AwsRagChat.Infrastructure/DependencyInjection/CloudProviderServiceCollectionExtensions.cs`

### Dependencies
*   `Azure.Extensions.AspNetCore.Configuration.Secrets`
*   `Amazon.Extensions.Configuration.SystemsManager`

### Risks
*   Inability to connect to Key Vault/Secrets Manager during local development could prevent application startup.
*   Network latency during initialization could slow down API startup times.

### Validation Approach
1.  **Local Developer Fallback:** Verify that the API falls back to user secrets or local environmental variables if key vault permissions are absent.
2.  **Cloud Bootstrapping:** In AWS and Azure hosting environments, verify that the application successfully bootstraps configuration parameters using managed identities (IRSA or Azure Managed Identity).

### Definition Of Done
*   Sensitive secrets (e.g. `CosmosDb:AuthKey`, `AzureOpenAi:ApiKey`, `VectorStore:ApiKey`) bind successfully to options classes from cloud key vaults.

---

## Phase 6 Commit 4: Polly Resilience and Retry Policies

### Goal
Configure transient fault tolerance retry policies using Polly for all external API endpoints, model APIs, and database clients.

### Why It Is Needed
Network drops, database query limits (e.g., Cosmos DB RU exhaustion / DynamoDB Write Limits), and model API rate limiting (HTTP 429) can crash active operations. Adding robust retry and back-off logic ensures system stability.

### Priority
*   **P1**

### Files Expected To Change
*   `AwsRagChat.Infrastructure/DependencyInjection.cs`
*   `AwsRagChat.Infrastructure/Persistence/CosmosDbUserRepository.cs` (and other Cosmos repositories)
*   `AwsRagChat.Infrastructure/Persistence/DynamoDbUserRepository.cs` (and other DynamoDB repositories)
*   `AwsRagChat.Infrastructure/Services/EntraUserRoleService.cs`
*   `AwsRagChat.Infrastructure/Services/AzureAiSearchVectorStore.cs`
*   `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`
*   `AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs`
*   `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`

### Dependencies
*   `Polly` NuGet package.

### Risks
*   Improperly configured retry parameters (e.g., waiting too long between retries) could exhaust the thread pool or block request pipelines.

### Validation Approach
1.  **Fault Injection:** Use unit/integration mocks to return HTTP 429 and HTTP 503 errors from external dependencies.
2.  **Policy Assertion:** Assert that the Polly retry policies intercept the exceptions, execute exponential back-off schedules, and recover without throwing execution crashes to the user.

### Definition Of Done
*   Retry policies are active across all external database repositories, vector stores, LLM model clients, and directory integrations.

---

## Phase 6 Commit 5: Unified Observability and Structured Telemetry

### Goal
Integrate OpenTelemetry tracing, metrics, and structured JSON logs to capture cross-platform latency, telemetry metrics, and exceptions.

### Why It Is Needed
Troubleshooting RAG performance bottlenecks (e.g., slow embedding retrieval or response generation times) requires distributed tracing and structured log monitoring across both cloud paths.

### Priority
*   **P2**

### Files Expected To Change
*   `AwsRagChat.Api/Program.cs`
*   `AwsRagChat.Infrastructure/DependencyInjection.cs`
*   `AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs`
*   `AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs`
*   `AwsRagChat.Infrastructure/Services/AzureAiSearchVectorStore.cs`
*   `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`

### Dependencies
*   `OpenTelemetry.Extensions.Hosting`
*   `OpenTelemetry.Instrumentation.AspNetCore`
*   `OpenTelemetry.Exporter.OpenTelemetryProtocol` (OTLP)

### Risks
*   Performance overhead from high trace-sampling rates.
*   Data egress costs when transmitting trace metrics to cloud monitors.

### Validation Approach
1.  **Local Trace Verification:** Run the application locally and verify OTLP traces publish successfully to a local collector (e.g., Jaeger or Zipkin).
2.  **Context Propagation:** Assert that logs capture the correct `TraceId` and `SpanId` across the Web API and Ingestion workflows.

### Definition Of Done
*   Structured JSON logging format is active in production.
*   End-to-end trace telemetry is exported for RAG query and document ingestion flows.

---

## Phase 6 Commit 6: Infrastructure as Code (IaC) Templates

### Goal
Define standard Infrastructure as Code (IaC) templates for both cloud providers.

### Why It Is Needed
Ensures consistent, reproducible deployments of the platform’s cloud architectures, preventing drifts and resource configuration mismatches.

### Priority
*   **P2**

### Files Expected To Change
*   New files under `/deploy` directory in the repository (e.g., `/deploy/aws` and `/deploy/azure`).

### Dependencies
*   Terraform CLI, Azure Bicep CLI, or AWS CloudFormation tooling.

### Risks
*   Desynchronization between the templates and manually configured development environments.

### Validation Approach
1.  **Template Linting:** Run linter and plan tools (`terraform plan`, `az bicep build`) to verify syntax validity.
2.  **Staging Deployment Run:** Deploy the environment templates to isolated staging resource groups and run core tests.

### Definition Of Done
*   IaC templates deploy all required databases, storage containers, indices, and role permissions correctly without manual steps.

---

# Production Readiness Assessment

## Security Readiness
*   **Secret Protection:** Plaintext strings are removed. Managed Secrets Providers load credentials dynamically at execution time.
*   **RBAC Alignment:** Infrastructure configurations use managed identities (`DefaultAzureCredential` / AWS IAM roles) instead of long-lived access keys.
*   **Identity Provider Security:** Audiences are validated for Microsoft Entra tokens; Cognito tokens validate client IDs and access constraints.

## Operational Readiness
*   **Configuration Drift:** Mitigated by infrastructure templates.
*   **Platform Fail-Safe:** Invalid `CloudProvider` configuration values crash immediately at startup with detailed diagnostic messages.
*   **Transient Resiliency:** Transient networking drops or resource throttling are resolved by back-off policies.

## Deployment Readiness
*   **Zero-Downtime Releases:** API endpoints contain no stateful local operations. Multi-node cloud deployments can run seamless rolling upgrades.
*   **Asynchronous Processing:** Ingestion tasks execute asynchronously on event-driven runners.

## Monitoring Readiness
*   **RAG Trace Tracking:** OpenTelemetry collects duration and latency metrics for embedding generation, vector matching, database lookups, and generation phases.
*   **Structured Logs:** Standardized formats simplify analysis in cloud dashboards (CloudWatch/Log Analytics).

## Scalability Readiness
*   **Database Scaling:** Storage layers scale independently (Cosmos DB Autoscale / DynamoDB On-Demand).
*   **Vector Search Scaling:** Azure AI Search and AWS OpenSearch scale replicas and partitions horizontally.

---

# Success Criteria

Phase 6 is declared complete when the following criteria are verified:
1.  **Dependency-Free Controllers:** API controllers compile and run in both AWS and Azure modes with zero provider-specific constructor parameters.
2.  **Automated Azure Ingestion:** Uploading a file in Azure mode successfully triggers OCR layout parsing, chunking, embedding generation, database persistence, and vector indexing.
3.  **Secret Store Integration:** The application boots in staging environments without reading database connection strings or API keys from plain-text files.
4.  **Resilient Execution:** System recovers gracefully from transient 429 and 503 exceptions without user-facing failures.
5.  **Telemetry Verification:** API request logs and ingestion span metrics export successfully using OpenTelemetry.

---

# Future Roadmap Recommendation

## Phase 7: Advanced RAG and Contextual Enhancements
*   **Hybrid Search:** Combine sparse keyword querying with dense vector searches.
*   **Reranking Architectures:** Introduce cross-encoder models (e.g., Cohere/BGE Reranker) to evaluate retrieved chunk relevance before model generation.
*   **Dynamic Metadata Extraction:** Auto-extract entity relationships during chunking to support advanced graph-style filters.

## Phase 8: Multi-Region Failover & Performance Tuning
*   **Active-Active Deployments:** Configure database replication (Cosmos DB Multi-Region / DynamoDB Global Tables) for multi-region setups.
*   **Vector Index Tuning:** Optimize HNSW index parameters (M, efConstruction) and cache lifetimes.
*   **Cross-Cloud Migration Framework:** Automated tools to migrate chunks, documents, and conversation histories from AWS DynamoDB/S3 to Azure Cosmos DB/Blob Storage.
