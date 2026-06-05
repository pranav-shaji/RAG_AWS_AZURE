# Phase 5 Commit 4 Plan: Cosmos DB Persistence Layer Integration

This document outlines the design and implementation details for swapping the AWS DynamoDB storage layer with Azure Cosmos DB (NoSQL API) as part of Phase 5.

---

## 1. Files to Create

*   **`AwsRagChat.Infrastructure/Options/CosmosDbOptions.cs`**:
    *   Holds settings for `Endpoint`, `AuthKey` (for local/key credentials), `DatabaseName`, and container name definitions (`DocumentsContainer`, `ChunksContainer`, `ConversationsContainer`, `UsersContainer`).
*   **`AwsRagChat.Infrastructure/Persistence/CosmosDbDocumentRepository.cs`**:
    *   Implements `IDocumentRepository` using `Microsoft.Azure.Cosmos`.
*   **`AwsRagChat.Infrastructure/Persistence/CosmosDbChunkRepository.cs`**:
    *   Implements `IChunkRepository` using `Microsoft.Azure.Cosmos`.
*   **`AwsRagChat.Infrastructure/Persistence/CosmosDbConversationRepository.cs`**:
    *   Implements `IConversationRepository` using `Microsoft.Azure.Cosmos`.
*   **`AwsRagChat.Infrastructure/Persistence/CosmosDbUserRepository.cs`**:
    *   Implements `IUserRepository` using `Microsoft.Azure.Cosmos`.
*   **`AwsRagChat.Infrastructure/Persistence/CosmosDbDocumentStatusService.cs`**:
    *   Implements `IDocumentStatusService` using `Microsoft.Azure.Cosmos`.

---

## 2. Files to Modify

*   **`AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj`**:
    *   Add package reference for `Microsoft.Azure.Cosmos`.
*   **`AwsRagChat.Infrastructure/DependencyInjection.cs`**:
    *   Register `CosmosDbOptions` and bind configuration keys from neutral sections.
    *   Register the Cosmos Client and scoped repositories.

---

## 3. Azure SDK Packages Required

We will add the following NuGet package:
*   `Microsoft.Azure.Cosmos` (v3.x, the official SDK for Cosmos DB NoSQL API).

---

## 4. Container Design

We will configure 4 containers within a single Cosmos DB database (e.g. `AwsRagChatDb`):

| Container Name | Description | Partition Key | Id Format |
| :--- | :--- | :--- | :--- |
| **`Documents`** | Stores uploaded document records | `/ownerUserId` | `documentId` (e.g. GUID) |
| **`Chunks`** | Stores document text chunks and embeddings | `/documentId` | `chunkId` (e.g. GUID) |
| **`Conversations`** | Stores session metadata and message logs | `/ownerUserId` | `session_{sessionId}` or `msg_{messageId}` |
| **`Users`** | Stores registered enterprise users and roles | `/id` | `userId` (e.g. cognito/entra sub) |

---

## 5. Partition Key Strategy

1.  **`Documents` (`/ownerUserId`)**:
    *   *Rationale*: Isolation by tenant/user. Queries fetching user documents (`GetDocumentsByUserAsync`), counting user documents (`GetDocumentCountAsync`), and locating hash duplicates (`FindByOwnerAndHashAsync`) are scoped to a single logical partition.
    *   *Trade-off*: Direct lookup by `documentId` (`GetDocumentByIdAsync`) requires a cross-partition query: `SELECT * FROM c WHERE c.id = @id`. Given low volume and fast indexing, this cross-partition query is fast and negligible in cost.
2.  **`Chunks` (`/documentId`)**:
    *   *Rationale*: Chunks are generated in bulk for specific documents. Partitioning by `documentId` keeps all chunks for a document in a single physical container partition, making chunk retrieval for search matching and file viewers extremely cost-effective.
    *   *Trade-off*: Fetching all chunks for a user (`GetAllChunksAsync`) will run as a cross-partition query filtered on `ownerUserId`.
3.  **`Conversations` (`/ownerUserId`)**:
    *   *Rationale*: Emulates DynamoDB single-table pattern. All sessions and messages for a specific user reside in the same logical partition. Messages can be queried, added, and deleted within a single partition context.
4.  **`Users` (`/id`)**:
    *   *Rationale*: Since the lookup key is always `userId`, partitioning on `/id` guarantees direct point reads.

---

## 6. Mapping: DynamoDB vs Cosmos DB Schema

Cosmos DB requires an `id` string property on every document. Property names will be normalized to camelCase.

### Document Record

```json
// DynamoDB Schema
{
  "DocumentId": "doc-123",
  "OwnerUserId": "user-456",
  "FileName": "test.pdf",
  "S3Key": "uploads/test.pdf",
  "FileHash": "hashabc",
  "Status": "INDEXED",
  "ChunkCount": 10,
  "PageCount": 2,
  "IsAdminDocument": false,
  "AllowedRoles": ["User"],
  "CreatedAtUtc": "2026-06-05T12:00:00Z",
  "UpdatedAtUtc": "2026-06-05T12:00:00Z"
}

// Cosmos DB Document Schema
{
  "id": "doc-123",
  "ownerUserId": "user-456",
  "fileName": "test.pdf",
  "storageKey": "uploads/test.pdf",
  "fileHash": "hashabc",
  "status": "INDEXED",
  "chunkCount": 10,
  "pageCount": 2,
  "isAdminDocument": false,
  "allowedRoles": ["User"],
  "createdAtUtc": "2026-06-05T12:00:00Z",
  "updatedAtUtc": "2026-06-05T12:00:00Z"
}
```

### Document Chunk

```json
// Cosmos DB Chunk Schema
{
  "id": "chunk-789",
  "documentId": "doc-123",
  "ownerUserId": "user-456",
  "fileName": "test.pdf",
  "storageKey": "uploads/test.pdf",
  "pageNumber": 1,
  "section": "Intro",
  "heading": "Background",
  "chunkOrder": 0,
  "text": "Extracted chunk content...",
  "isAdminDocument": false,
  "allowedRoles": ["User"],
  "embedding": [0.012, -0.054, 0.984],
  "createdAtUtc": "2026-06-05T12:00:00Z"
}
```

### Conversation (Single-Table Simulation)

*   **Session Document**:
    ```json
    {
      "id": "session_sess-abc",
      "entityType": "SESSION",
      "sessionId": "sess-abc",
      "ownerUserId": "user-456",
      "title": "Topic chat",
      "summary": "Summary...",
      "createdAtUtc": "2026-06-05T12:00:00Z",
      "updatedAtUtc": "2026-06-05T12:00:00Z",
      "lastMessageAtUtc": "2026-06-05T12:00:00Z",
      "messageCount": 1,
      "isArchived": false
    }
    ```
*   **Message Document**:
    ```json
    {
      "id": "msg_msg-xyz",
      "entityType": "MESSAGE",
      "sessionId": "sess-abc",
      "messageId": "msg-xyz",
      "ownerUserId": "user-456",
      "role": "user",
      "content": "Hello world",
      "createdAtUtc": "2026-06-05T12:00:00Z",
      "tokensApprox": 2,
      "responseType": "text",
      "dataJson": "",
      "chartDataJson": "",
      "citations": []
    }
    ```

---

## 7. Query Compatibility Considerations

*   **Paging Continuation Tokens**:
    *   DynamoDB scans use custom base64 encoded strings of `LastEvaluatedKey`.
    *   In Cosmos DB, pagination will map cleanly using the SDK's `QueryRequestOptions.ContinuationToken` and `FeedResponse.ContinuationToken`, passing the string token directly through the `NextToken` property of the `PagedResult<T>` structure.
*   **Aggregations & Stats**:
    *   DynamoDB scans the whole table to compile analytics metrics (e.g., total pages/chunks).
    *   Cosmos DB will perform efficient SQL aggregate queries:
        ```sql
        SELECT VALUE SUM(c.chunkCount) FROM c
        SELECT VALUE SUM(c.pageCount) FROM c
        SELECT VALUE COUNT(1) FROM c WHERE c.status = 'INDEXED'
        ```
        This significantly reduces resource usage (RUs) and latency.

---

## 8. Status Tracking Compatibility Considerations

The `DocumentStatusService` handles tracking of background ingestion tasks (uploading, OCR extraction, embedding, and indexing).
*   **Problem**: Writing full documents during status changes could overwrite metadata (like `allowedRoles` or `isAdminDocument`) written by the primary API thread.
*   **Solution**: Use the Cosmos DB **Patch API** (`PatchItemAsync`) to update only the modified status properties (`status`, `updatedAtUtc`, `chunkCount`, `pageCount`, etc.) atomically, avoiding full document replacement.
*   **Example Patch Operation**:
    ```csharp
    var patchOperations = new[]
    {
        PatchOperation.Set("/status", status),
        PatchOperation.Set("/updatedAtUtc", DateTime.UtcNow)
    };
    await container.PatchItemAsync<ExistingDocumentRecord>(documentId, new PartitionKey(ownerUserId), patchOperations);
    ```

---

## 9. Build Verification Plan

1.  **Add package reference**:
    ```powershell
    dotnet add AwsRagChat.Infrastructure.csproj package Microsoft.Azure.Cosmos
    ```
2.  **Verify project compile**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj
    dotnet build AwsRagChat/AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj
    dotnet build AwsRagChat/AwsRagChat.Api/AwsRagChat.Api.csproj
    ```
3.  **Validate full solution**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.slnx
    ```
