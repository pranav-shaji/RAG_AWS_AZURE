# Phase 6 Commit 2 Plan: Azure Ingestion Trigger Architecture (V2)

This revised plan details the trigger architecture and project restructuring required to host Azure Functions triggers in a dedicated host project, separating them from the shared ingestion library and AWS Lambda handlers.

---

## 1. New Project Structure

We will restructure the serverless ingestion logic to achieve clean separation of concerns and runtime isolation:

```
AwsRagChat/
  ├── AwsRagChat.Ingestion/                 [Shared Library]
  │     ├── Aws/
  │     │     └── AwsDocumentProcessor.cs
  │     ├── Azure/
  │     │     └── AzureDocumentProcessor.cs
  │     ├── Services/
  │     │     ├── ChunkingService.cs
  │     │     ├── DocumentIngestionPipeline.cs
  │     │     └── TextExtractionService.cs
  │     └── AwsRagChat.Ingestion.csproj     (OutputType = Library)
  │
  ├── AwsRagChat.Ingestion.Aws/             [AWS Lambda Host - RENAME/NEW]
  │     ├── Handlers/
  │     │     ├── S3DocumentIngestionFunction.cs
  │     │     └── TextractCompletionFunction.cs
  │     ├── appsettings.json
  │     ├── aws-lambda-tools-defaults.json
  │     └── AwsRagChat.Ingestion.Aws.csproj (OutputType = Library)
  │
  └── AwsRagChat.Ingestion.Azure/           [Azure Functions Host - NEW]
        ├── Triggers/
        │     ├── BlobStorageIngestionTrigger.cs
        │     └── OcrCompletionQueueTrigger.cs
        ├── Program.cs
        ├── host.json
        ├── local.settings.json
        └── AwsRagChat.Ingestion.Azure.csproj (OutputType = Exe)
```

---

## 2. Project csproj Requirements

### Shared Ingestion Library (`AwsRagChat.Ingestion.csproj`)
*   **Target Framework:** `net8.0`
*   **Output Type:** `Library` (default class library).
*   **Package References:** Core SDKs needed for code compilation (`AWSSDK.Textract`, `Azure.AI.DocumentIntelligence`, `PdfPig`). Contains NO serverless runtime dependencies (no `Amazon.Lambda.*` or `Microsoft.Azure.Functions.Worker.*` packages).

### AWS Ingestion Host (`AwsRagChat.Ingestion.Aws.csproj`)
*   **Target Framework:** `net8.0`
*   **Project References:** `AwsRagChat.Ingestion.csproj`, `AwsRagChat.Infrastructure.csproj`.
*   **Package References:** `Amazon.Lambda.Core`, `Amazon.Lambda.S3Events`, `Amazon.Lambda.SQSEvents`, `Amazon.Lambda.Serialization.SystemTextJson`.

### Azure Ingestion Host (`AwsRagChat.Ingestion.Azure.csproj`)
*   **Target Framework:** `net8.0`
*   **Output Type:** `Exe`
*   **Project References:** `AwsRagChat.Ingestion.csproj`, `AwsRagChat.Infrastructure.csproj`.
*   **Package References:**
    *   `Microsoft.Azure.Functions.Worker`
    *   `Microsoft.Azure.Functions.Worker.Sdk` (Build tool generator)
    *   `Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs`
    *   `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues`
    *   `Microsoft.Extensions.Hosting`
    *   `Microsoft.Extensions.DependencyInjection`

---

## 3. Azure Functions Startup Model (`Program.cs`)

We will use the ASP.NET Core Dependency Injection bootstrap builder in the Isolated Worker model:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((hostContext, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Configuration is implicitly read from local.settings.json / Environment variables
        var configuration = hostContext.Configuration;
        
        // Composition and bindings are loaded dynamically
        // AzureIngestionComposition.Create registrations are registered here
    })
    .Build();

host.Run();
```

---

## 4. Blob Trigger Design (`BlobStorageIngestionTrigger.cs`)

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AwsRagChat.Ingestion.Azure;
using AwsRagChat.Application.Models;
using System.IO;

namespace AwsRagChat.Ingestion.Azure.Triggers;

public class BlobStorageIngestionTrigger
{
    private readonly ILogger<BlobStorageIngestionTrigger> _logger;

    public BlobStorageIngestionTrigger(ILogger<BlobStorageIngestionTrigger> logger)
    {
        _logger = logger;
    }

    [Function("BlobStorageIngestionTrigger")]
    public async Task Run(
        [BlobTrigger("uploads/{userId}/{documentId}/{fileName}", Connection = "AzureWebJobsStorage")] Stream blobStream,
        string userId,
        string documentId,
        string fileName,
        FunctionContext context)
    {
        _logger.LogInformation("Blob trigger processing blob. Name: {fileName}, Size: {length} bytes", fileName, blobStream.Length);
        
        // Execute the AzureIngestionComposition pipeline.
        // If async OCR starts, serialize metadata and operation ID and write to the Azure Queue.
    }
}
```

---

## 5. Queue Trigger Design (`OcrCompletionQueueTrigger.cs`)

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AwsRagChat.Ingestion.Azure.Triggers;

public class OcrCompletionQueueTrigger
{
    private readonly ILogger<OcrCompletionQueueTrigger> _logger;

    public OcrCompletionQueueTrigger(ILogger<OcrCompletionQueueTrigger> logger)
    {
        _logger = logger;
    }

    [Function("OcrCompletionQueueTrigger")]
    public async Task Run(
        [QueueTrigger("ocr-completion-queue", Connection = "AzureWebJobsStorage")] string message,
        FunctionContext context)
    {
        _logger.LogInformation("Queue trigger checking OCR status. Msg: {message}", message);
        
        // Check operation status using AzureDocumentProcessor.GetCompletedOcrResultAsync.
        // If incomplete, throw to trigger queue retry (exponential back-off).
        // If complete, execute ingestion pipeline and finish.
    }
}
```

---

## 6. Dependency Injection Strategy

*   **Shared Ingestion composition:** `AzureIngestionComposition.Create` handles registration of standard storage providers (`AzureBlobStorageService`), repositories (`CosmosDbChunkRepository`), embedding engines (`AzureOpenAiEmbeddingService`), and vector search indexers (`AzureAiSearchVectorStore`).
*   **Function Host DI:** The Azure Function `Program.cs` bootstraps these resolved service implementations directly, injecting them into the constructor parameters of the trigger classes (`BlobStorageIngestionTrigger` / `OcrCompletionQueueTrigger`).
*   This approach completely isolates dependency registrations, preventing AWS clients from bleeding into the Azure Function runtime.

---

## 7. Runtime Flow

```
[Azure Blob Upload] 
  --> BlobStorageIngestionTrigger (Fires)
        --> Marks processing in Cosmos DB
        --> Calls AzureDocumentProcessor.ExtractAsync()
              ├── Case A: Direct Text/Sync Image OCR 
              │     --> Executes Ingestion Pipeline
              │     --> Indexes to Azure AI Search & Cosmos DB
              │     --> Completed!
              └── Case B: Async OCR started (returns operationId)
                    --> Marks OCR_STARTED status in Cosmos DB
                    --> Enqueues job metadata to 'ocr-completion-queue'
                            --> OcrCompletionQueueTrigger (Fires)
                                  --> GetCompletedOcrResultAsync()
                                  ├── In-Progress: Throws (Causes retry back-off)
                                  └── Completed: Indexes chunks to DB & Search
```

---

## 8. Files To Create

*   `AwsRagChat.Ingestion.Azure/AwsRagChat.Ingestion.Azure.csproj`
*   `AwsRagChat.Ingestion.Azure/Program.cs`
*   `AwsRagChat.Ingestion.Azure/host.json`
*   `AwsRagChat.Ingestion.Azure/local.settings.json`
*   `AwsRagChat.Ingestion.Azure/Triggers/BlobStorageIngestionTrigger.cs`
*   `AwsRagChat.Ingestion.Azure/Triggers/OcrCompletionQueueTrigger.cs`

---

## 9. Files To Modify

*   `AwsRagChat.slnx` (register the new `AwsRagChat.Ingestion.Azure` project and clean up references).
*   `AwsRagChat.Ingestion/AwsRagChat.Ingestion.csproj` (remove unused deployment files, set output type to class library).
*   `AwsRagChat.Ingestion.Aws/AwsRagChat.Ingestion.Aws.csproj` [NEW/RENAME] (restructure and point to standard shared references).

---

## 10. Build Validation Strategy

Verify that compiling the new multi-project structure runs successfully with zero conflicts:
```powershell
dotnet build AwsRagChat.slnx
```

---

## 11. Deployment Validation Strategy

1.  **Local Testing (Azurite):** Run Azurite local emulators. Use Azure Functions Core Tools (`func start`) locally to verify triggering, queueing, and retry routines.
2.  **Staging Deployment Verification:** Deploy the compiled host project `AwsRagChat.Ingestion.Azure` to an Azure Functions Consumption plan. Verify that container files trigger processing and that the queue successfully processes async Document Intelligence callbacks.
