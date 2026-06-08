using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Services;
using AwsRagChat.Infrastructure.AI;
using AwsRagChat.Infrastructure.Options;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Storage;
using AwsRagChat.Infrastructure.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AwsRagChat.Infrastructure.Services;
using Amazon.CognitoIdentityProvider;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;


namespace AwsRagChat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<S3Options>(options =>
        {
            // First load from legacy
            configuration.GetSection(S3Options.SectionName).Bind(options);

            // Override/fallback with Storage section
            var storageSection = configuration.GetSection("Storage");
            if (storageSection.Exists())
            {
                var bucket = storageSection["BucketOrContainerName"];
                if (!string.IsNullOrEmpty(bucket))
                {
                    options.BucketName = bucket;
                }
            }
        });

        services.Configure<DynamoDbOptions>(
            configuration.GetSection(DynamoDbOptions.SectionName));

        services.Configure<BedrockOptions>(options =>
        {
            // First load from legacy Bedrock section
            configuration.GetSection(BedrockOptions.SectionName).Bind(options);

            // Override/fallback with Embedding:ModelId if present
            var embeddingModelId = configuration["Embedding:ModelId"];
            if (!string.IsNullOrEmpty(embeddingModelId))
            {
                options.EmbeddingModelId = embeddingModelId;
            }

            // Override/fallback with Chat:ModelId if present
            var chatModelId = configuration["Chat:ModelId"];
            if (!string.IsNullOrEmpty(chatModelId))
            {
                options.ChatModelId = chatModelId;
            }
        });

        services.Configure<ConversationStorageOptions>(
            configuration.GetSection(ConversationStorageOptions.SectionName));

        services.Configure<CognitoOptions>(options =>
        {
            // First load from legacy Cognito section
            configuration.GetSection(CognitoOptions.SectionName).Bind(options);

            // Fallback from neutral Identity section
            var identitySection = configuration.GetSection("Identity");
            if (identitySection.Exists())
            {
                var clientId = identitySection["ClientId"];
                if (!string.IsNullOrEmpty(clientId))
                {
                    options.AppClientId = clientId;
                }

                var authority = identitySection["Authority"];
                if (!string.IsNullOrEmpty(authority))
                {
                    try
                    {
                        var uri = new Uri(authority);
                        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (pathSegments.Length > 0)
                        {
                            options.UserPoolId = pathSegments[^1];
                        }

                        var hostParts = uri.Host.Split('.');
                        if (hostParts.Length > 1 && hostParts[0].Equals("cognito-idp", StringComparison.OrdinalIgnoreCase))
                        {
                            options.Region = hostParts[1];
                        }
                    }
                    catch
                    {
                        // Ignore parsing errors, keep legacy values
                    }
                }
            }
        });

        services.Configure<IdentityOptions>(options =>
        {
            // First load from new Identity section
            configuration.GetSection(IdentityOptions.SectionName).Bind(options);

            // Fallback from legacy Cognito section
            var cognitoSection = configuration.GetSection("Cognito");
            if (cognitoSection.Exists())
            {
                if (string.IsNullOrEmpty(options.ClientId))
                {
                    options.ClientId = cognitoSection["AppClientId"] ?? string.Empty;
                }
                if (string.IsNullOrEmpty(options.Authority))
                {
                    options.Authority = cognitoSection["Authority"] ?? string.Empty;
                }
            }
        });

        services.Configure<AzureOpenAiOptions>(options =>
        {
            var embeddingSection = configuration.GetSection("Embedding");
            if (embeddingSection.Exists())
            {
                options.EmbeddingDeploymentName = embeddingSection["ModelId"] ?? string.Empty;
            }

            var chatSection = configuration.GetSection("Chat");
            if (chatSection.Exists())
            {
                options.ChatDeploymentName = chatSection["ModelId"] ?? string.Empty;
            }

            configuration.GetSection(AzureOpenAiOptions.SectionName).Bind(options);
        });

        services.Configure<AzureAiSearchOptions>(options =>
        {
            var openSearchSection = configuration.GetSection("OpenSearch");
            if (openSearchSection.Exists())
            {
                options.Endpoint = openSearchSection["Endpoint"] ?? string.Empty;
                options.IndexName = openSearchSection["IndexName"] ?? "rag-index";
            }

            configuration.GetSection(AzureAiSearchOptions.SectionName).Bind(options);
        });

        services.Configure<CosmosDbOptions>(options =>
        {
            configuration.GetSection(CosmosDbOptions.SectionName).Bind(options);
        });

        services.Configure<EntraIdOptions>(options =>
        {
            configuration.GetSection(EntraIdOptions.SectionName).Bind(options);
        });

        // Register Core, Cloud-Agnostic Services
        services.AddScoped<RetrievalService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<ChatService>();
        services.AddScoped<IUserApprovalService, UserApprovalService>();
        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

        // Register central Polly resilience pipelines
        services.AddCustomResiliencePipelines();

        return services;
    }
}
