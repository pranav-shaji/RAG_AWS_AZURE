using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Azure;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Ingestion.Aws;
using ExtractedDocument = AwsRagChat.Ingestion.Services.ExtractedDocument;

var host = new HostBuilder()
    .ConfigureAppConfiguration((hostContext, configBuilder) =>
    {
        var tempConfig = configBuilder.Build();
        var cloudProvider = tempConfig["CloudProvider"];

        if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            var vaultUriStr = tempConfig["AZURE_KEY_VAULT_URI"];
            if (!string.IsNullOrWhiteSpace(vaultUriStr))
            {
                if (Uri.TryCreate(vaultUriStr, UriKind.Absolute, out var vaultUri))
                {
                    configBuilder.AddAzureKeyVault(vaultUri, new DefaultAzureCredential());
                }
                else
                {
                    Console.WriteLine($"WARNING: AZURE_KEY_VAULT_URI is not a valid absolute URI: {vaultUriStr}");
                }
            }
            else
            {
                Console.WriteLine("WARNING: CloudProvider is Azure but AZURE_KEY_VAULT_URI is not configured. Key Vault config provider was not added.");
            }
        }
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((hostContext, services) =>
    {
        var configuration = hostContext.Configuration;

        // Register worker-level Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configure OpenTelemetry to capture custom tracing and metrics, exporting to Azure Monitor
        var appInsightsConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    tracing.AddSource("AwsRagChat")
                           .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddMeter("AwsRagChat")
                           .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
                });
        }

        services.AddSingleton<AwsIngestionServices>(sp =>
        {
            var conf = sp.GetRequiredService<IConfiguration>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return AzureIngestionComposition.Create(conf, loggerFactory);
        });

        services.AddSingleton<IDocumentProcessor>(sp => sp.GetRequiredService<AwsIngestionServices>().DocumentProcessor);
        services.AddSingleton<IDocumentStatusService>(sp => sp.GetRequiredService<AwsIngestionServices>().DocumentStatusService);
        services.AddSingleton<IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult>>(sp =>
            sp.GetRequiredService<AwsIngestionServices>().DocumentIngestionPipeline);
    })
    .Build();

host.Run();
