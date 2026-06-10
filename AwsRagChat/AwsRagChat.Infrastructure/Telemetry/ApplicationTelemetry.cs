using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AwsRagChat.Infrastructure.Telemetry;

public static class ApplicationTelemetry
{
    public const string ServiceName = "AwsRagChat";
    public const string ServiceVersion = "1.0.0";
    
    // Activity source for Distributed Tracing
    public static readonly ActivitySource Source = new(ServiceName, ServiceVersion);
    
    // Meters for custom metrics collection
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);
    
    // Counters & Histograms
    public static readonly Counter<long> LlmTokenCounter = Meter.CreateCounter<long>("llm.tokens.used", "Count of chat/embedding tokens consumed");
    public static readonly Counter<long> PollyRetryCounter = Meter.CreateCounter<long>("resilience.polly.retries", "Count of Polly strategy retry executions");
    public static readonly Counter<long> CacheHitCounter = Meter.CreateCounter<long>("cache.hits", "Count of cache hits");
    public static readonly Counter<long> CacheMissCounter = Meter.CreateCounter<long>("cache.misses", "Count of cache misses");
    public static readonly Histogram<double> DbLatencyHistogram = Meter.CreateHistogram<double>("database.operation.latency", "ms", "Database and storage latency histogram");
    public static readonly Histogram<double> OcrDurationHistogram = Meter.CreateHistogram<double>("ocr.job.duration", "ms", "OCR extraction job duration");
    public static readonly Counter<long> OcrJobCounter = Meter.CreateCounter<long>("ocr.jobs", "Count of OCR jobs by status");
}
