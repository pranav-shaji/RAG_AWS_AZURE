using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Ingestion.Azure;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Services;
using ExtractedDocument = AwsRagChat.Ingestion.Services.ExtractedDocument;

var host = new HostBuilder()
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
