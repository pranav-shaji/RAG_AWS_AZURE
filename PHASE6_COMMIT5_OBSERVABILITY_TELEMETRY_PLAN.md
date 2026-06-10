# PHASE6_COMMIT5_OBSERVABILITY_TELEMETRY_PLAN

## Executive Summary
To achieve production-grade visibility, self-healing diagnostics, and cloud-neutral monitoring, this plan introduces a unified **OpenTelemetry (OTel)** telemetry strategy. 

Rather than integrating vendor-locked SDKs (e.g., raw AWS X-Ray SDKs or legacy Microsoft Application Insights SDKs) directly into our core logic, we leverage **OpenTelemetry** as the standard telemetry API. Under this model, the API and ingestion layers produce standard logs, traces, and metrics. These are then routed to CloudWatch (on AWS) or Azure Monitor / Application Insights (on Azure) using lightweight, cloud-specific exporters.

This plan reviews the codebase's telemetry status, identifies key gaps (including cross-boundary correlation), and presents a production-grade implementation design that maintains zero code dependencies on cloud-specific logging services within the Application and Domain layers.

---

## Codebase Verification Findings

1. **Existing ILogger Usage**:
   * **API**: Controller classes (like [`DocumentsController.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/Controllers/DocumentsController.cs) and [`ChatController.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/Controllers/ChatController.cs)) receive standard `ILogger<T>` injections. Console and Debug logging providers are configured inside `Program.cs`.
   * **Infrastructure**: AI services ([`AzureOpenAiChatService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/AzureOpenAiChatService.cs), [`BedrockChatCompletionService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/AI/BedrockChatCompletionService.cs)), vector store services ([`AzureAiSearchVectorStore.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/AzureAiSearchVectorStore.cs), [`OpenSearchService.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Infrastructure/Services/OpenSearchService.cs)), and repositories receive `ILogger<T>` injections and log key actions.
   * **Ingestion (Azure)**: Triggers ([`BlobStorageIngestionTrigger.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Azure/Triggers/BlobStorageIngestionTrigger.cs)) inject `ILogger<T>` natively via Azure Functions DI.
   * **Ingestion (AWS)**: Lambda triggers ([`S3DocumentIngestionFunction.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Aws/Handlers/S3DocumentIngestionFunction.cs)) do *not* inject `ILogger`. They log via `ILambdaContext.Logger.LogLine` directly to the stdout/stderr Lambda execution stream.

2. **Existing OpenTelemetry Packages**:
   * **Status**: None. No project in the solution references any OpenTelemetry package.

3. **Existing Application Insights Integration**:
   * **Status**: None. Neither [`AwsRagChat.Api.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/AwsRagChat.Api.csproj) nor [`AwsRagChat.Ingestion.Azure.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Azure/AwsRagChat.Ingestion.Azure.csproj) references Application Insights logging packages.

4. **Existing CloudWatch Logging Integration**:
   * **Status**: None. No AWS logging providers (e.g. `AWS.Logger.AspNetCore` or `Serilog.Sinks.AwsCloudWatch`) are installed. AWS components write purely to standard output streams, which are captured natively by AWS Lambda/App Runner and routed to CloudWatch.

5. **Current Correlation ID Handling**:
   * **Status**: None. There is no middleware, logging scope, or header propagation mechanism (e.g., `X-Correlation-ID`) in place to track requests across API and async worker boundaries.

6. **Existing Metrics Collection Mechanisms**:
   * **Status**: None. No runtime metrics, cache stats, Polly retry counters, or token usage counters are collected.

7. **Azure Functions Telemetry Configuration**:
   * **Status**: Relying entirely on default Azure Function host console log forwarding. No worker-level Application Insights telemetry module is configured.

8. **AWS Lambda Telemetry Configuration**:
   * **Status**: Relying entirely on raw `context.Logger.LogLine` text output. No structured JSON logger or AWS X-Ray trace context mapping is registered.

---

## Identified Telemetry Gaps & Risks

### 1. Telemetry Gaps
* **Distributed Tracing Boundary Gap**: When the API writes an ingestion request to SQS or Azure Storage Queues, the transaction flow is severed. The worker triggers read and execute requests with a brand-new trace context, preventing end-to-end tracing.
* **Unstructured Logs**: Serverless outputs are formatted as raw text lines, making it extremely difficult to parse parameters (like `UserId`, `DocumentId`, or `ExecutionDuration`) in CloudWatch Insight queries or Log Analytics.
* **LLM Token Tracking & Cost Monitoring**: No metrics capture embedding or chat token consumption, hiding production cost drivers.
* **Polly Event Observability**: Polly retry attempts, timeouts, and circuit breaker trips occur silently in logs without raising specific telemetry metrics or alerts.

### 2. Observability Risks
* **Cold-Start Telemetry Bloat**: Installing heavy, legacy monitoring packages on serverless environments (specifically AWS Lambda) can increase cold start overheads by 500ms+.
* **Logging Duplication**: Catching exceptions in repositories, logging them, and then re-throwing them to controllers results in duplicated error messages, bloating storage costs.
* **Context Bleed**: Without structured logging scopes, sensitive customer details or raw document text might accidentally bleed into persistent telemetry stores.

---

## Proposed Architecture (OTel-Centric)

```
[Client Request] 
      │ (X-Correlation-ID)
      ▼
┌──────────────┐
│  Api Host    │ ───► [OTel Traces/Metrics] ──► OTLP / Azure App Insights Exporter
└──────┬───────┘
       │ (W3C TraceContext Injected into SQS/Storage Queue Message)
       ▼
┌──────────────┐
│ Queue/S3/Blob│
└──────┬───────┘
       │ (W3C TraceContext Extracted from Message Envelope)
       ▼
┌──────────────┐
│ Worker Host  │ ───► [OTel Traces/Metrics] ──► AWS Distro for OTel (ADOT) / App Insights
└──────────────┘
```

### Core Design Rules
1. **W3C Trace Context Standard**: We will utilize the W3C Trace Context standard (`traceparent` header) to propagate correlation IDs across API, queues, and serverless triggers.
2. **Structural JSON Logging**: All logs in production will be formatted as structured JSON containing fields: `Timestamp`, `LogLevel`, `TraceId`, `SpanId`, `Message`, `Exception`, and `CorrelationId`.
3. **No Direct Vendor Lock-in**: Application code will use standard `System.Diagnostics.Activity` (for tracing) and `System.Diagnostics.Metrics.Meter` (for metrics). Exporters will be registered purely at the entry-points (`Program.cs`).

---

## Detailed Implementation Plan

### Phase 1: Package References

We will introduce lightweight OpenTelemetry NuGet packages.

#### 1. Core Infrastructure & API Dependencies
Modify `AwsRagChat.Infrastructure.csproj` and `AwsRagChat.Api.csproj`:
* `OpenTelemetry` (v1.9.0)
* `OpenTelemetry.Extensions.Hosting` (v1.9.0)
* `OpenTelemetry.Instrumentation.AspNetCore` (v1.9.0)
* `OpenTelemetry.Instrumentation.Http` (v1.9.0)
* `OpenTelemetry.Exporter.Console` (v1.9.0)
* `OpenTelemetry.Exporter.OpenTelemetryProtocol` (v1.9.0)
* `Azure.Monitor.OpenTelemetry.AspNetCore` (v1.2.0) — *Azure-specific OTel exporter*

#### 2. Azure Ingestion Dependencies
Modify `AwsRagChat.Ingestion.Azure.csproj`:
* `Microsoft.Azure.Functions.Worker.ApplicationInsights` (v1.2.0)
* `OpenTelemetry.Instrumentation.Runtime` (v1.9.0)

#### 3. AWS Ingestion Dependencies
Modify `AwsRagChat.Ingestion.Aws.csproj`:
* `OpenTelemetry.Exporter.OpenTelemetryProtocol` (v1.9.0)
* `Amazon.Lambda.Serialization.SystemTextJson` (v2.4.5) — *For JSON serialization optimization*

---

### Phase 2: Core Telemetry Setup

#### 1. Custom Telemetry Activity & Metrics Source
Create a new file `AwsRagChat.Infrastructure/Telemetry/ApplicationTelemetry.cs` to declare standard telemetry hooks:
```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AwsRagChat.Infrastructure.Telemetry;

public static class ApplicationTelemetry
{
    private const string ServiceName = "AwsRagChat";
    
    // Activity source for Distributed Tracing
    public static readonly ActivitySource Source = new(ServiceName);
    
    // Meters for custom metrics collection
    public static readonly Meter Meter = new(ServiceName, "1.0.0");
    
    // Counters & Histograms
    public static readonly Counter<long> LlmTokenCounter = Meter.CreateCounter<long>("llm.tokens.used", "Count of chat/embedding tokens consumed");
    public static readonly Counter<long> PollyRetryCounter = Meter.CreateCounter<long>("resilience.polly.retries", "Count of Polly strategy retry executions");
    public static readonly Counter<long> CacheHitCounter = Meter.CreateCounter<long>("cache.hits", "Count of cache hits");
    public static readonly Counter<long> CacheMissCounter = Meter.CreateCounter<long>("cache.misses", "Count of cache misses");
    public static readonly Histogram<double> DbLatencyHistogram = Meter.CreateHistogram<double>("database.operation.latency", "ms", "Database latency histogram");
}
```

#### 2. Correlation ID Propagation Middleware
Create `AwsRagChat.Api/Middleware/CorrelationIdMiddleware.cs` to handle incoming API request headers and inject correlation context into `ILogger` scope:
* Check for header `X-Correlation-ID`. If missing, generate a new GUID.
* Inject it into the current `Activity` as a tag.
* Inject it into the `ILogger` scope using `ILogger.BeginScope()`.
* Add `X-Correlation-ID` to the outgoing API HTTP response headers.

---

### Phase 3: Polly & Infrastructure Instrumentation

#### 1. Polly Resilience Telemetry
Update `ResilienceRegistration.cs` to listen to Polly strategy events (`OnRetry`, `OnCircuitBreak`, `OnTimeout`) and report custom metrics:
```csharp
OnRetry = args =>
{
    ApplicationTelemetry.PollyRetryCounter.Add(1, 
        new KeyValuePair<string, object?>("pipeline", pipelineName),
        new KeyValuePair<string, object?>("exception", args.Outcome.Exception?.GetType().Name));
    return default;
}
```

#### 2. Caching Instrumentation
Update `RedisCacheService.cs` to record hit and miss telemetry counters:
* Increment `CacheHitCounter` when a cache lookup returns a value.
* Increment `CacheMissCounter` when a lookup returns `null` or a connection failure occurs.

#### 3. LLM Token Instrumentation
Update `AzureOpenAiChatService.cs`, `AzureOpenAiEmbeddingService.cs`, `BedrockChatCompletionService.cs`, and `EmbeddingBatchService.cs` to extract token usage from API responses:
* Increment `LlmTokenCounter` with dimensions: `model`, `provider`, and `type` (e.g. `prompt` vs `completion`).

---

### Phase 4: Async Boundary Trace Propagation

#### 1. Queue Injection (API Side)
When pushing an ingestion trigger request to Amazon SQS or Azure Queue Storage, inject the current trace context (`traceparent`) into the message metadata/attributes:
```csharp
var messageAttributes = new Dictionary<string, MessageAttributeValue>();
if (Activity.Current != null)
{
    messageAttributes["traceparent"] = new MessageAttributeValue
    {
        DataType = "String",
        StringValue = Activity.Current.Id // W3C traceparent standard
    };
}
```

#### 2. Queue Extraction (Ingestion Triggers)
When receiving queue messages inside AWS Lambda (`S3DocumentIngestionFunction`) or Azure Functions (`BlobStorageIngestionTrigger`):
* Parse `traceparent` from the SQS Message Attribute or Azure Storage Queue Metadata.
* Start a new `Activity` using `ApplicationTelemetry.Source.StartActivity("QueueTriggerProcess", ActivityKind.Consumer, parentContext)`.
* This ensures that the tracing graph remains fully linked end-to-end.

---

### Phase 5: Exporter Registrations

#### 1. API Exporters (`Program.cs`)
Register OpenTelemetry services conditionally based on `CloudProvider`:
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("AwsRagChat")
               .AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation();

        if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString);
        }
        else
        {
            tracing.AddOtlpExporter(); // AWS ADOT / OTel Collector routing
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("AwsRagChat")
               .AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation();
               
        if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            metrics.AddAzureMonitorMetricExporter();
        }
        else
        {
            metrics.AddOtlpExporter();
        }
    });
```

#### 2. Azure Functions Telemetry (`Program.cs` - Azure Ingestion)
Add Application Insights support to the host builder:
```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();
```

#### 3. AWS Lambda Structured Telemetry (`AwsIngestionComposition.cs` - AWS Ingestion)
To keep Lambda execution overhead minimal:
* Replace default console logging with a lightweight structured JSON Console Formatter package (e.g., `Microsoft.Extensions.Logging.Console.Json`).
* This formats all console-routed logs as standard single-line JSON, allowing CloudWatch to parse fields automatically without requiring complex regex metrics filters.

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
| :--- | :--- | :--- |
| **Serverless Cold Start Overhead** | Medium | AWS Ingestion will not use OTLP exporters or network tracing processors natively inside short-lived Lambdas. Instead, we use structured JSON logging to stdout, allowing the **AWS X-Ray daemon** or **CloudWatch Log Router** to parse and compile trace spans asynchronously. |
| **High Telemetry Storage Costs** | High | Apply trace sampling rates (e.g., 10% of successful requests, 100% of failed requests) in production configuration to prevent trace storage overruns. |
| **Sensitive Data Bleeding** | High | Restrict logging levels so that raw user chat inputs, document texts, and credentials are never stored in log messages, trace attributes, or custom telemetry tags. |

---

## Verification Plan

### 1. Automated Tests
* Execute unit tests verifying that `CorrelationIdMiddleware` injects correlation tags into `Activity.Current` when incoming request headers contain `X-Correlation-ID`.
* Mock message envelope structures to assert that W3C traceparents are successfully injected and extracted across serialization limits.

### 2. Manual Verification
* Deploy Api and Ingestion triggers to a local/staging environment.
* Execute a document upload and query. Verify in Jaeger, AWS X-Ray, or Application Insights transaction timelines that a single transaction contains:
  1. API request trace
  2. Queue message publish trace
  3. Worker function trigger run trace
  4. Vector indexing / search database trace.
* Verify CloudWatch / Log Analytics dashboard displaying:
  * Count of Polly retries by strategy name.
  * Custom token usage charts.
  * Redis cache hits/miss ratio.
