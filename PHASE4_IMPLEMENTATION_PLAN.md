# Phase 4 Implementation Plan: Naming Leakage & Identity Normalization

## Goal Description

Eliminate AWS-specific naming leakage and normalize authentication structures across the repository. This phase ensures the application layer, domain layer, frontend contracts, and configurations are cloud-neutral, transitioning all references from AWS-specific concepts (such as `S3Key` and `Cognito`) to provider-agnostic equivalents (such as `StorageKey` and configurable identity claims).

---

## User Review Required

> [!WARNING]
> **Data Scheme Compatibility & Existing Records**
> Renaming `S3Key` to `StorageKey` in the code must not break compatibility with existing documents and chunks in DynamoDB or existing indices in Amazon OpenSearch Serverless. We must use mapping logic in persistence and serialization layers to preserve existing data attributes.

> [!IMPORTANT]
> **Authentication Claim Changes**
> Replacing hardcoded Cognito-specific groups claims (`cognito:groups`) with configurable identity claims requires alignment between the backend options, the Angular frontend session extraction, and the OIDC identity provider group mappings.

---

## Open Questions

*   **Database Key Migration**: Should we rename the physical database table attributes in DynamoDB, or is it preferred to use property mapping (`[DynamoDBProperty("S3Key")]` / attribute maps) to preserve the existing database state without migrations? *(Plan assumes property mapping is preferred to minimize risk).*
*   **Vector Index Naming**: Should the JSON field indexing key in OpenSearch remain `s3Key` to avoid re-indexing documents, or should it change to `storageKey`? *(Plan assumes keeping the JSON indexing key as `s3Key` under the hood is preferred for backward compatibility).*

---

## Proposed Changes

### 1. Naming Leakage Normalization (S3Key -> StorageKey)

#### Domain & Application DTOs
*   **Rename `S3Key` to `StorageKey`** in the domain entity:
    *   [DocumentChunk.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Domain/Entities/DocumentChunk.cs)
*   **Rename `S3Key` to `StorageKey`** in application interface records and DTOs:
    *   [IDocumentRepository.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IDocumentRepository.cs) (`ExistingDocumentRecord`)
    *   [UploadDocumentResponse.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/DTOs/UploadDocumentResponse.cs)
*   **Update references** in [ChatService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Services/ChatService.cs), [RetrievalService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Services/RetrievalService.cs), and [PromptBuilder.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Services/PromptBuilder.cs).

#### Infrastructure Persistence & Vector Store
*   **Preserve DB mapping**: In [DynamoDbChunkRepository.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs) and [DynamoDbDocumentRepository.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs), map the C# `StorageKey` property to the DynamoDB attribute `"S3Key"` to avoid breaking data compatibility.
*   **Preserve search mapping**: In [OpenSearchService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/OpenSearchService.cs), continue map/indexing under the JSON key `"s3Key"`, but map it to `StorageKey` in the returned C# objects.

#### Ingestion Status Tracking
*   **Rename parameter names** from `s3Key` to `storageKey` in the status service:
    *   [IDocumentStatusService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IDocumentStatusService.cs)
    *   [DocumentStatusService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/Services/DocumentStatusService.cs)
    *   Ensure the DynamoDB item map attributes persist the value to the DynamoDB column `"S3Key"` for compatibility.

#### Frontend API and Interface
*   **Rename `s3Key` to `storageKey`** in [api.ts](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/app/api.ts) interfaces (`UploadResponse` and `DocumentMetadata`).

---

### 2. Identity & Claims Normalization

#### Claims & Principal Extensions (Backend)
*   **Define configurable claim options**: Add `IdentityOptions.cs` to hold configured claim names such as `RoleClaimType` or `GroupsClaim`.
*   **Update Claims principal helper**: Modify [ClaimsPrincipalExtensions.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/Security/ClaimsPrincipalExtensions.cs) to retrieve roles dynamically from configuration rather than hardcoding `"cognito:groups"`.
*   **Update user controllers**: Refactor [UserRoleController.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/Controllers/UserRoleController.cs) to match roles using the configured claim key.

#### Frontend Authentication (Angular)
*   **Neutralize environments**: In [environment.ts](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/environments/environment.ts) and [environment.prod.ts](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/environments/environment.prod.ts), rename:
    *   `cognitoClientId` -> `authClientId`
    *   `cognitoDomain` -> `authDomain`
    *   `cognitoScopes` -> `authScopes`
*   **Introduce configurable role claim**: Add `authRoleClaim` to environments (defaulting to `"cognito:groups"`).
*   **Refactor auth service**: Update [auth.ts](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/app/auth.ts) to utilize these neutral properties and dynamically extract group roles using the configured `authRoleClaim`.
*   **Clean display text**: Remove Cognito-specific labels from [app.html](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/app/app.html) and [chat-page.html](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/aws-rag-chat-ui/src/app/pages/chat-page/chat-page.html) (e.g., replace "Login with Cognito" with "Sign In" or "Login", and remove references to "Amazon Bedrock", "OpenSearch", and "Titan Embeddings" in generic layout footers).

---

### 3. Configuration & Switched Modules

#### Configuration Normalization
*   Introduce unified, provider-neutral configuration sections in [appsettings.json](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/appsettings.json) and Ingestion [appsettings.json](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion/appsettings.json):
    ```json
    {
      "CloudProvider": "AWS",
      "Storage": {
        "Provider": "AWS",
        "BucketOrContainerName": "aws-rag-chat-docs-prajwal"
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
        "IndexName": "rag-index-v2",
        "Endpoint": "https://..."
      },
      "Identity": {
        "Provider": "AWS",
        "Authority": "https://cognito-idp.us-east-1.amazonaws.com/...",
        "ClientId": "...",
        "GroupsClaim": "cognito:groups"
      }
    }
    ```
*   Update Infrastructure register modules to bind neutral options while keeping fallback to existing sections (`S3`, `Bedrock`, `Cognito`, `OpenSearch`) to support backward compatibility.

---

## Commit Boundaries

### Commit 1: Domain, DTO, and Repository Naming Cleanup
*   **Files**:
    *   `AwsRagChat.Domain/Entities/DocumentChunk.cs`
    *   `AwsRagChat.Application/Interfaces/IDocumentRepository.cs`
    *   `AwsRagChat.Application/DTOs/UploadDocumentResponse.cs`
    *   `AwsRagChat.Infrastructure/Persistence/DynamoDbChunkRepository.cs`
    *   `AwsRagChat.Infrastructure/Persistence/DynamoDbDocumentRepository.cs`
*   **Changes**: Rename `S3Key` -> `StorageKey`. Bind to DynamoDB attribute `"S3Key"` for data mapping compatibility.

### Commit 2: Ingestion & Retrieval Naming Normalization
*   **Files**:
    *   `AwsRagChat.Application/Interfaces/IDocumentStatusService.cs`
    *   `AwsRagChat.Ingestion/Services/DocumentStatusService.cs`
    *   `AwsRagChat.Ingestion/Services/ChunkingService.cs`
    *   `AwsRagChat.Ingestion/Services/ChunkPersistenceService.cs`
    *   `AwsRagChat.Application/Services/RetrievalService.cs`
    *   `AwsRagChat.Application/Services/ChatService.cs`
    *   `AwsRagChat.Application/Services/PromptBuilder.cs`
    *   `AwsRagChat.Infrastructure/Services/OpenSearchService.cs`
*   **Changes**: Update status tracking and query/logs logic to reference `StorageKey` and generic terminology instead of S3, Bedrock, and OpenSearch references.

### Commit 3: Backend Claims & Identity Normalization
*   **Files**:
    *   `AwsRagChat.Api/Security/ClaimsPrincipalExtensions.cs`
    *   `AwsRagChat.Api/Controllers/UserRoleController.cs`
    *   `AwsRagChat.Infrastructure/Aws/AwsAuthenticationExtensions.cs`
    *   `AwsRagChat.Infrastructure/DependencyInjection.cs`
*   **Changes**: Implement configured claim keys for group role extraction rather than hardcoded `"cognito:groups"`.

### Commit 4: Frontend Auth & UI Neutralization
*   **Files**:
    *   `aws-rag-chat-ui/src/environments/environment.ts`
    *   `aws-rag-chat-ui/src/environments/environment.prod.ts`
    *   `aws-rag-chat-ui/src/app/api.ts`
    *   `aws-rag-chat-ui/src/app/auth.ts`
    *   `aws-rag-chat-ui/src/app/app.html`
    *   `aws-rag-chat-ui/src/app/pages/chat-page/chat-page.html`
*   **Changes**: Map frontend upload and metadata contracts to `storageKey`. Normalize OIDC auth domain, client ID, and scopes variables. Clean up display text naming.

### Commit 5: Normalized Configuration Switches
*   **Files**:
    *   `AwsRagChat.Api/appsettings.json`
    *   `AwsRagChat.Ingestion/appsettings.json`
    *   `AwsRagChat.Infrastructure/DependencyInjection.cs`
*   **Changes**: Structure and parse provider-neutral configuration sections with backward-compatible fallbacks.

---

## Verification Plan

### Automated Tests & Builds
*   Build the full solution:
    ```powershell
    dotnet build AwsRagChat.slnx
    ```
*   Build frontend Angular app:
    ```powershell
    cd aws-rag-chat-ui
    npm run build
    ```

### Terminology Leak Checks
Verify that no direct AWS-specific leaks remain in core projects:
```powershell
rg -n "S3Key" AwsRagChat/AwsRagChat.Domain AwsRagChat/AwsRagChat.Application
rg -n "cognito:groups" AwsRagChat/AwsRagChat.Api
rg -n "Amazon Bedrock|OpenSearch" aws-rag-chat-ui/src/app
```

---

## Expected Readiness Improvement

Completing Phase 4 will elevate the multi-cloud readiness of the platform significantly.

| Subsystem | Phase 3 Score | Expected Phase 4 Score | Key Improvement |
| :--- | :---: | :---: | :--- |
| **Storage** | 7.0/10 | **9.0/10** | `S3Key` naming leakage eliminated across APIs, domain, and frontend contracts. |
| **Identity** | 3.0/10 | **8.0/10** | Hardcoded Cognito claims and Hosted UI dependencies neutralized. |
| **Frontend** | 3.0/10 | **9.0/10** | UI text and OIDC configurations are fully cloud-neutral. |
| **Ingestion** | 6.5/10 | **7.5/10** | Status pipeline uses generic `StorageKey` tracking. |

**Expected Overall Multi-Cloud Readiness Score: 8.0 / 10** *(Up from 6.4/10)*
