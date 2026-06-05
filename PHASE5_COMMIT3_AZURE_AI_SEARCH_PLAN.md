# Phase 5 Commit 3 Plan: Azure AI Search Vector Store Integration

This document outlines the implementation plan for the Azure AI Search integration as part of Phase 5.

---

## 1. Files to Create

*   **`AwsRagChat.Infrastructure/Options/AzureAiSearchOptions.cs`**: Holds settings for Endpoint, ApiKey (optional, for token credentials), and IndexName.
*   **`AwsRagChat.Infrastructure/Services/AzureAiSearchVectorStore.cs`**: Implements `IVectorStore` and `IVectorSearchService` utilizing the Azure AI Search SDK.

---

## 2. Files to Modify

*   **`AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj`**: Add package reference `Azure.Search.Documents`.
*   **`AwsRagChat.Infrastructure/DependencyInjection.cs`**: Register `AzureAiSearchOptions` and bind configuration fallbacks.

---

## 3. Azure SDK Packages Required

We will add the following NuGet package:
*   `Azure.Search.Documents` (v11.x is the stable library for Azure AI Search, providing classes like `SearchIndexClient`, `SearchClient`, and `VectorizedQuery`).

---

## 4. Mapping: OpenSearchService vs AzureAiSearchVectorStore

| Feature | OpenSearchService | AzureAiSearchVectorStore |
| :--- | :--- | :--- |
| **SDK Clients** | `IOpenSearchClient` | `SearchIndexClient` (for index management)<br>`SearchClient` (for document indexing/search) |
| **Authentication** | AWS SigV4 signed connection (`AwsSigV4HttpConnection`) | `AzureKeyCredential` (API Key) or `DefaultAzureCredential` (Token credential) |
| **Upload Method** | `IndexAsync` with anonymous document objects | `IndexDocumentsAsync` with structured model instances or dictionaries |
| **Key Generation** | `"{UserId}:{DocId}:{ChunkId}"` | `"{UserId}_{DocId}_{ChunkId}"` (Sanitized key without colons) |
| **Filter Format** | Nesting boolean arrays (`should`, `must`, `filter`) | OData filter string query (e.g. `"(ownerUserId eq 'X' or isAdminDocument eq true) and allowedRoles/any(r: search.in(r, 'Admin,admin'))"`) |
| **Vector Search** | JSON kNN query map on `embedding` property | `VectorizedQuery` with `VectorizableText` or float array `Vector` values |

---

## 5. Vector Schema Compatibility Concerns

*   **Key Field Validation**:
    *   *Concern*: Azure AI Search keys only accept letters, digits, underscores, dashes, and equal signs. OpenSearch IDs contain colons (`:`), which are forbidden in Azure AI Search.
    *   *Solution*: Sanitize the index ID during upload and retrieval by replacing colons with underscores:
        ```csharp
        var sanitizedKey = $"{chunk.OwnerUserId}_{chunk.DocumentId}_{chunk.ChunkId}"
            .Replace(":", "_");
        ```
*   **Distance Metric & Profiles**:
    *   *Concern*: OpenSearch indices configure vector search parameters on the index creation payload. Azure AI Search requires defining `VectorSearchProfile` and `HnswAlgorithmConfiguration` explicitly during index creation.
    *   *Solution*: Default the distance metric to `VectorSearchAlgorithmMetric.Cosine` to ensure similarity score sorting behaves consistently with OpenSearch.

---

## 6. Indexing Behavior Compatibility

Azure AI Search does not implicitly create indexes on the first document push if they do not exist. Therefore, `AzureAiSearchVectorStore` must verify index presence during startup or before the first upload:
1.  Check if the index exists using `SearchIndexClient.GetIndexNamesAsync()`.
2.  If missing, programmatically define and build the index fields:
    *   `SearchField` of type `SearchFieldDataType.Single` (dimensions = 1536 or configured model length).
    *   Set up a `VectorSearch` configuration with `HnswAlgorithmConfiguration` containing Cosine metric parameters.
    *   Bind the vector search profile to the vector field.

---

## 7. Search Behavior Compatibility

*   **OData Role Filter Syntax**:
    *   OpenSearch builds an array of casing variants for the role filter. In Azure AI Search, we can perform collection lookups using standard OData search helper functions like `search.in`:
        ```csharp
        var roleVariants = string.Join(",", BuildRoleVariants(currentUserRole));
        var filter = $"allowedRoles/any(r: search.in(r, '{roleVariants}'))";
        ```
*   **Document Isolation & Admin Sharing**:
    *   Construct the overall filter matching OpenSearch's logical behavior:
        ```csharp
        var filterBuilder = new List<string>();
        
        // Scope documents
        if (searchSharedAdminDocuments)
        {
            filterBuilder.Add("isAdminDocument eq true");
            if (sharedDocumentIds != null && sharedDocumentIds.Any())
            {
                var docIds = string.Join(",", sharedDocumentIds);
                filterBuilder.Add($"search.in(documentId, '{docIds}')");
            }
        }
        else
        {
            filterBuilder.Add($"(ownerUserId eq '{ownerUserId}' or isAdminDocument eq true)");
        }
        
        // Apply document ID filter if specifically targeted
        if (!string.IsNullOrEmpty(documentId))
        {
            filterBuilder.Add($"documentId eq '{documentId}'");
        }
        
        // Apply role permissions
        var roles = string.Join(",", BuildRoleVariants(currentUserRole));
        filterBuilder.Add($"allowedRoles/any(r: search.in(r, '{roles}'))");
        
        var odataFilter = string.Join(" and ", filterBuilder);
        ```

---

## 8. Build Verification Plan

1.  Add package reference to the project:
    ```powershell
    dotnet add AwsRagChat.Infrastructure.csproj package Azure.Search.Documents
    ```
2.  Build the project and solution:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj
    dotnet build AwsRagChat/AwsRagChat.slnx
    ```
3.  Verify that both OpenSearch and Azure AI Search options compile cleanly under `DependencyInjection.cs`.
