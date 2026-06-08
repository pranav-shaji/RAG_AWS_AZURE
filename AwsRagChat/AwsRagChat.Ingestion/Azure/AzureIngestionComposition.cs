using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.AI.OpenAI;
using Azure.AI.DocumentIntelligence;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Options;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Storage;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Services;
using AwsRagChat.Infrastructure.AI;
using AwsRagChat.Infrastructure.Options;
using AwsRagChat.Ingestion.Aws;
using System;
using System.IO;
using System.Net.Http;

namespace AwsRagChat.Ingestion.Azure;

public static class AzureIngestionComposition
{
    public static AwsIngestionServices Create(IConfiguration configuration)
    {
        // 1. Load options
        var storageOptions = new AzureStorageOptions();
        var storageSection = configuration.GetSection("Storage");
        if (storageSection.Exists())
        {
            storageOptions.BucketOrContainerName = storageSection["BucketOrContainerName"] ?? string.Empty;
        }
        configuration.GetSection(AzureStorageOptions.SectionName).Bind(storageOptions);
        var storageIOptions = Microsoft.Extensions.Options.Options.Create(storageOptions);

        var cosmosDbOptions = new CosmosDbOptions();
        configuration.GetSection(CosmosDbOptions.SectionName).Bind(cosmosDbOptions);
        var cosmosDbIOptions = Microsoft.Extensions.Options.Options.Create(cosmosDbOptions);

        var azureOpenAiOptions = new AzureOpenAiOptions();
        var embeddingSection = configuration.GetSection("Embedding");
        if (embeddingSection.Exists())
        {
            azureOpenAiOptions.EmbeddingDeploymentName = embeddingSection["ModelId"] ?? string.Empty;
        }
        var chatSection = configuration.GetSection("Chat");
        if (chatSection.Exists())
        {
            azureOpenAiOptions.ChatDeploymentName = chatSection["ModelId"] ?? string.Empty;
        }
        configuration.GetSection(AzureOpenAiOptions.SectionName).Bind(azureOpenAiOptions);
        var azureOpenAiIOptions = Microsoft.Extensions.Options.Options.Create(azureOpenAiOptions);

        var vectorStoreOptions = new AzureAiSearchOptions();
        var openSearchSection = configuration.GetSection("OpenSearch");
        if (openSearchSection.Exists())
        {
            vectorStoreOptions.Endpoint = openSearchSection["Endpoint"] ?? string.Empty;
            vectorStoreOptions.IndexName = openSearchSection["IndexName"] ?? "rag-index";
        }
        configuration.GetSection(AzureAiSearchOptions.SectionName).Bind(vectorStoreOptions);
        var vectorStoreIOptions = Microsoft.Extensions.Options.Options.Create(vectorStoreOptions);

        var docProcessingOptions = new AzureDocumentProcessingOptions();
        var docProcessingSection = configuration.GetSection("DocumentProcessing");
        if (docProcessingSection.Exists())
        {
            docProcessingOptions.Endpoint = docProcessingSection["Endpoint"] ?? string.Empty;
            docProcessingOptions.ApiKey = docProcessingSection["ApiKey"] ?? string.Empty;
            docProcessingOptions.ModelId = docProcessingSection["ModelId"] ?? "prebuilt-layout";
        }
        configuration.GetSection(AzureDocumentProcessingOptions.SectionName).Bind(docProcessingOptions);
        var docProcessingIOptions = Microsoft.Extensions.Options.Options.Create(docProcessingOptions);

        // 2. Instantiate CosmosClient
        CosmosClient cosmosClient;
        var cosmosClientOptions = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };
        if (!string.IsNullOrWhiteSpace(cosmosDbOptions.AuthKey))
        {
            cosmosClient = new CosmosClient(cosmosDbOptions.Endpoint, cosmosDbOptions.AuthKey, cosmosClientOptions);
        }
        else
        {
            cosmosClient = new CosmosClient(cosmosDbOptions.Endpoint, new DefaultAzureCredential(), cosmosClientOptions);
        }

        // 3. Instantiate other clients and services
        var storageProvider = new AzureBlobStorageService(storageIOptions);
        var chunkRepository = new CosmosDbChunkRepository(cosmosClient, cosmosDbIOptions);
        var documentRepository = new CosmosDbDocumentRepository(cosmosClient, cosmosDbIOptions);
        var documentStatusService = new CosmosDbDocumentStatusService(cosmosClient, cosmosDbIOptions);
        var openSearchService = new AzureAiSearchVectorStore(vectorStoreIOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureAiSearchVectorStore>.Instance);

        var textExtractionService = new TextExtractionService();
        var documentProcessor = new AzureDocumentProcessor(
            textExtractionService,
            docProcessingIOptions,
            storageProvider);

        var chunkingService = new ChunkingService();
        
        var embeddingProvider = new AzureOpenAiEmbeddingService(
            azureOpenAiIOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureOpenAiEmbeddingService>.Instance);

        var documentIngestionPipeline = new DocumentIngestionPipeline(
            chunkingService,
            embeddingProvider,
            chunkRepository,
            documentStatusService,
            openSearchService);

        return new AwsIngestionServices
        {
            DocumentProcessor = documentProcessor,
            DocumentStatusService = documentStatusService,
            DocumentIngestionPipeline = documentIngestionPipeline
        };
    }
}
