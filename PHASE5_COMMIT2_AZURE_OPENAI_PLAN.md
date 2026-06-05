# Phase 5 Commit 2 Plan: Azure OpenAI Integration

This document outlines the implementation plan for the Azure OpenAI integration as part of Phase 5.

---

## 1. Existing Interfaces

The Azure OpenAI integration must implement the following provider-neutral interfaces located in the Application layer:

### `IEmbeddingProvider` (inherits `IEmbeddingService`)
*   **Path**: [IEmbeddingService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IEmbeddingService.cs) & [IEmbeddingProvider.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IEmbeddingProvider.cs)
*   **Method**: `Task<List<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)`

### `IChatProvider` (inherits `IChatCompletionService`)
*   **Path**: [IChatCompletionService.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IChatCompletionService.cs) & [IChatProvider.cs](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Application/Interfaces/IChatProvider.cs)
*   **Methods**:
    *   `GenerateAnswerAsync(...)`: Generates a grounded response based on relevant document chunks.
    *   `GenerateGeneralAnswerAsync(...)`: Generates general responses using conversation history.
    *   `GenerateKnowledgeOverviewAsync(...)`: Classifies and summarizes current document domain information.

---

## 2. Azure Implementations Required

### `AzureOpenAiEmbeddingService`
*   **Location**: `AwsRagChat.Infrastructure/AI/AzureOpenAiEmbeddingService.cs`
*   **Responsibilities**: Connects to the Azure OpenAI Service Endpoint, invokes the configured embedding deployment name, extracts the float array, and normalizes the embedding vector using standard L2 normalization.

### `AzureOpenAiChatService`
*   **Location**: `AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs`
*   **Responsibilities**: Resolves LLM chat prompts using the `PromptBuilder` utility, calls Azure OpenAI chat models, maps operational parameters (temperature, token limits), and extracts the text content output.

---

## 3. Azure SDK Packages Required

We will install the following packages in `AwsRagChat.Infrastructure.csproj`:
*   `Azure.AI.OpenAI` (v2.x is standard for modern .NET 8 apps, utilizing the unified `AzureOpenAIClient`, `ChatClient`, and `EmbeddingClient` classes).
*   `Azure.Identity` (already installed in Commit 1; utilized to resolve `DefaultAzureCredential`).

---

## 4. Configuration Model Required

We will create a new options configuration class `AzureOpenAiOptions.cs` under `AwsRagChat.Infrastructure/Options/`:
```csharp
namespace AwsRagChat.Infrastructure.Options;

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAi";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string EmbeddingDeploymentName { get; set; } = string.Empty;

    public string ChatDeploymentName { get; set; } = string.Empty;
}
```

This configuration will be fed by the neutral `Embedding` and `Chat` config blocks during DI setup:
*   `Embedding:Provider` = `"Azure"`
*   `Embedding:ModelId` -> maps to `EmbeddingDeploymentName`
*   `Chat:Provider` = `"Azure"`
*   `Chat:ModelId` -> maps to `ChatDeploymentName`

---

## 5. Mapping: BedrockEmbeddingService vs AzureOpenAiEmbeddingService

| Feature | BedrockEmbeddingService | AzureOpenAiEmbeddingService |
| :--- | :--- | :--- |
| **SDK client** | `IAmazonBedrockRuntime` | `AzureOpenAIClient` (which yields `EmbeddingClient`) |
| **Model ID resolving** | Reads `Bedrock:EmbeddingModelId` | Reads `AzureOpenAi:EmbeddingDeploymentName` |
| **Payload assembly** | Serializes anonymous object `new { inputText = text }` | Passes string parameter directly to `GenerateEmbeddingAsync(text)` |
| **Response Parsing** | Reads response body stream, parses JSON, and finds `"embedding"` array | Reads `ClientResult<ReadOnlyMemory<float>>`, calls `Value.ToFloats()` |
| **Normalization** | Performs L2 normalization (`NormalizeEmbedding`) | Must perform identical L2 normalization to ensure consistent vector space scoring. |

---

## 6. Mapping: BedrockChatCompletionService vs AzureOpenAiChatService

| Feature | BedrockChatCompletionService | AzureOpenAiChatService |
| :--- | :--- | :--- |
| **SDK Client** | `IAmazonBedrockRuntime` | `AzureOpenAIClient` (which yields `ChatClient`) |
| **Model ID** | `Bedrock:ChatModelId` | `AzureOpenAi:ChatDeploymentName` |
| **Prompt Assembly** | Calls `PromptBuilder` helpers | Calls identical `PromptBuilder` helpers |
| **Payload/Messages** | Serializes custom Bedrock message arrays with `inferenceConfig` | Creates `UserChatMessage` and feeds `ChatCompletionOptions` |
| **Inference Tuning** | Temperature: 0.1 (grounded) / 0.2 (general)<br>Max tokens: 4096 (grounded) / 700 (overview) | Temperature: 0.1 (grounded) / 0.2 (general)<br>Max tokens: 4096 (grounded) / 700 (overview) |
| **Response Extraction** | Enumerates `"output.message.content"` array in response JSON | Accesses `ChatCompletion.Content[0].Text` from result |

---

## 7. Risks and Compatibility Concerns

*   **API Shape Divergence (v1.x vs v2.x SDK)**:
    *   *Concern*: Older code examples use the legacy `OpenAIClient` from `Azure.AI.OpenAI` v1.0.0-beta.x. Modern applications must use `AzureOpenAIClient` from v2.0.0+.
    *   *Mitigation*: Target `AzureOpenAIClient` and the corresponding namespace `OpenAI.Chat` and `OpenAI.Embeddings` directly.
*   **Embedding Space Similarity Score**:
    *   *Concern*: Changing the embedding model (e.g. from AWS Titan to Azure OpenAI `text-embedding-3-small` or `text-embedding-ada-002`) changes the dimensionality and vector spaces. Querying an index built with Titan embeddings using OpenAI embeddings will result in garbage vector match results.
    *   *Mitigation*: The configuration must dictate indexing and vector searches. Ensure that the active cloud provider's embedding model aligns with the vector store database index name.
*   **Prompt String Compatibility**:
    *   *Concern*: Nova/Claude models on Bedrock might interpret system instructions slightly differently than GPT-4o models on Azure.
    *   *Mitigation*: The `PromptBuilder` is cloud-neutral. If prompt adjustments are needed, they should be isolated behind neutral prompt parameters in the future.

---

## 8. Build Verification Plan

1.  **Add package reference**:
    ```powershell
    dotnet add AwsRagChat.Infrastructure.csproj package Azure.AI.OpenAI
    ```
2.  **Verify project compile**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj
    dotnet build AwsRagChat/AwsRagChat.Api/AwsRagChat.Api.csproj
    ```
3.  **Validate full solution**:
    ```powershell
    dotnet build AwsRagChat/AwsRagChat.slnx
    ```
