using System;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Storage;
using AwsRagChat.Infrastructure.AI;
using AwsRagChat.Infrastructure.Services;

namespace AwsRagChat.Infrastructure.CloudProviders;

public static class AzureProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAzureProviderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 1. Register CosmosClient (isolated to Azure provider path only)
        services.AddSingleton<CosmosClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CosmosDbOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Endpoint))
                throw new InvalidOperationException("Cosmos DB Endpoint is missing.");

            var cosmosClientOptions = new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            };

            if (!string.IsNullOrWhiteSpace(options.AuthKey))
            {
                return new CosmosClient(options.Endpoint, options.AuthKey, cosmosClientOptions);
            }
            else
            {
                return new CosmosClient(options.Endpoint, new DefaultAzureCredential(), cosmosClientOptions);
            }
        });

        // 2. Register Cosmos DB Repositories
        services.AddScoped<CosmosDbDocumentRepository>();
        services.AddScoped<IUserRepository>(provider => provider.GetRequiredService<CosmosDbUserRepository>());
        services.AddScoped<CosmosDbUserRepository>();
        
        services.AddScoped<IDocumentRepository>(provider => provider.GetRequiredService<CosmosDbDocumentRepository>());
        services.AddScoped<IChunkRepository>(provider => provider.GetRequiredService<CosmosDbChunkRepository>());
        services.AddScoped<CosmosDbChunkRepository>();
        
        services.AddScoped<IConversationRepository>(provider => provider.GetRequiredService<CosmosDbConversationRepository>());
        services.AddScoped<CosmosDbConversationRepository>();

        services.AddScoped<IDocumentStatusService>(provider => provider.GetRequiredService<CosmosDbDocumentStatusService>());
        services.AddScoped<CosmosDbDocumentStatusService>();

        // 3. Register Blob Storage
        services.AddScoped<AzureBlobStorageService>();
        services.AddScoped<IStorageProvider>(provider => provider.GetRequiredService<AzureBlobStorageService>());
        services.AddScoped<IStorageService>(provider => provider.GetRequiredService<AzureBlobStorageService>());

        // 4. Register OpenAI Services
        services.AddScoped<AzureOpenAiEmbeddingService>();
        services.AddScoped<IEmbeddingProvider>(provider => provider.GetRequiredService<AzureOpenAiEmbeddingService>());
        services.AddScoped<IEmbeddingService>(provider => provider.GetRequiredService<AzureOpenAiEmbeddingService>());

        services.AddScoped<AzureOpenAiChatService>();
        services.AddScoped<IChatProvider>(provider => provider.GetRequiredService<AzureOpenAiChatService>());
        services.AddScoped<IChatCompletionService>(provider => provider.GetRequiredService<AzureOpenAiChatService>());

        // 5. Register Azure AI Search Vector Store
        services.AddScoped<AzureAiSearchVectorStore>();
        services.AddScoped<IVectorStore>(provider => provider.GetRequiredService<AzureAiSearchVectorStore>());
        services.AddScoped<IVectorSearchService>(provider => provider.GetRequiredService<AzureAiSearchVectorStore>());

        // 6. Register Microsoft Entra ID Role Service
        services.AddScoped<IUserRoleService, EntraUserRoleService>();

        // 7. Initialize common infrastructure (core app services and options bindings)
        services.AddInfrastructure(configuration);

        return services;
    }
}
