using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Azure;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
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
        var ingestionServices = AzureIngestionComposition.Create(configuration);

        services.AddSingleton<IDocumentProcessor>(ingestionServices.DocumentProcessor);
        services.AddSingleton<IDocumentStatusService>(ingestionServices.DocumentStatusService);
        services.AddSingleton<IIngestionPipeline<IngestionDocumentRequest, ExtractedDocument, IngestionPipelineResult>>(
            ingestionServices.DocumentIngestionPipeline);
    })
    .Build();

host.Run();
