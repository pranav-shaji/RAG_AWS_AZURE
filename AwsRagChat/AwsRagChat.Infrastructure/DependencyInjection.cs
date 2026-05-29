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

        services.AddScoped<IStorageService, S3StorageService>();

        services.AddAWSService<IAmazonCognitoIdentityProvider>();

        services.AddScoped<IChunkRepository, DynamoDbChunkRepository>();

        services.AddScoped<IDocumentRepository, DynamoDbDocumentRepository>();

        services.AddScoped<IConversationRepository, DynamoDbConversationRepository>();

        services.AddScoped<IUserRepository, DynamoDbUserRepository>();

        services.AddScoped<IEmbeddingService, BedrockEmbeddingService>();

        services.AddScoped<IChatCompletionService, BedrockChatCompletionService>();

        services.AddScoped<IVectorSearchService, OpenSearchService>();

        services.AddScoped<RetrievalService>();

        services.AddScoped<ConversationService>();

        services.AddScoped<ChatService>();

        services.AddScoped<IUserRoleService, CognitoUserRoleService>();

        services.AddScoped<IUserApprovalService, UserApprovalService>();

        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

        return services;
    }
}
