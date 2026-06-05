using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Services;
using AwsRagChat.Infrastructure.AI;
using AwsRagChat.Infrastructure.Options;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AwsRagChat.Infrastructure.Services;
using Amazon.CognitoIdentityProvider;

namespace AwsRagChat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<S3Options>(
            configuration.GetSection(S3Options.SectionName));

        services.Configure<DynamoDbOptions>(
            configuration.GetSection(DynamoDbOptions.SectionName));

        services.Configure<BedrockOptions>(
            configuration.GetSection(BedrockOptions.SectionName));

        services.Configure<ConversationStorageOptions>(
            configuration.GetSection(ConversationStorageOptions.SectionName));

        services.Configure<CognitoOptions>(
            configuration.GetSection(CognitoOptions.SectionName));

        services.Configure<IdentityOptions>(
            configuration.GetSection(IdentityOptions.SectionName));

        services.AddScoped<S3StorageService>();

        services.AddScoped<IStorageProvider>(provider =>
            provider.GetRequiredService<S3StorageService>());

        services.AddScoped<IStorageService>(provider =>
            provider.GetRequiredService<S3StorageService>());

        services.AddAWSService<IAmazonCognitoIdentityProvider>();

        services.AddScoped<IChunkRepository, DynamoDbChunkRepository>();

        services.AddScoped<IDocumentRepository, DynamoDbDocumentRepository>();

        services.AddScoped<IConversationRepository, DynamoDbConversationRepository>();

        services.AddScoped<IUserRepository, DynamoDbUserRepository>();

        services.AddScoped<BedrockEmbeddingService>();

        services.AddScoped<IEmbeddingProvider>(provider =>
            provider.GetRequiredService<BedrockEmbeddingService>());

        services.AddScoped<IEmbeddingService>(provider =>
            provider.GetRequiredService<BedrockEmbeddingService>());

        services.AddScoped<BedrockChatCompletionService>();

        services.AddScoped<IChatProvider>(provider =>
            provider.GetRequiredService<BedrockChatCompletionService>());

        services.AddScoped<IChatCompletionService>(provider =>
            provider.GetRequiredService<BedrockChatCompletionService>());

        services.AddScoped<OpenSearchService>();

        services.AddScoped<IVectorStore>(provider =>
            provider.GetRequiredService<OpenSearchService>());

        services.AddScoped<IVectorSearchService>(provider =>
            provider.GetRequiredService<OpenSearchService>());

        services.AddScoped<RetrievalService>();

        services.AddScoped<ConversationService>();

        services.AddScoped<ChatService>();

        services.AddScoped<IUserRoleService, CognitoUserRoleService>();

        services.AddScoped<IUserApprovalService, UserApprovalService>();

        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

        return services;
    }
}
