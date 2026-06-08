using Amazon;
using Amazon.BedrockRuntime;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Storage;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.AI;
using AwsRagChat.Infrastructure.Services;
using AwsRagChat.Infrastructure;

namespace AwsRagChat.Infrastructure.Aws;

public static class AwsProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAwsProviderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var awsRegion = configuration["AWS:Region"];
        if (string.IsNullOrWhiteSpace(awsRegion))
        {
            throw new InvalidOperationException("AWS:Region is required.");
        }

        // 1. Register AWS SDK Clients
        var awsOptions = new AWSOptions
        {
            Region = RegionEndpoint.GetBySystemName(awsRegion)
        };
        awsOptions.DefaultClientConfig.MaxErrorRetry = 1; // Prevent SDK-level retry amplification, let Polly handle it
        services.AddDefaultAWSOptions(awsOptions);

        services.AddAWSService<IAmazonS3>();
        services.AddAWSService<IAmazonDynamoDB>();
        services.AddAWSService<IAmazonBedrockRuntime>();
        services.AddAWSService<IAmazonCognitoIdentityProvider>();

        // 2. Register AWS Service Implementations
        services.AddScoped<S3StorageService>();
        services.AddScoped<IStorageProvider>(provider => provider.GetRequiredService<S3StorageService>());
        services.AddScoped<IStorageService>(provider => provider.GetRequiredService<S3StorageService>());

        services.AddScoped<IChunkRepository, DynamoDbChunkRepository>();
        services.AddScoped<IDocumentRepository, DynamoDbDocumentRepository>();
        services.AddScoped<IConversationRepository, DynamoDbConversationRepository>();
        services.AddScoped<IUserRepository, DynamoDbUserRepository>();

        services.AddScoped<BedrockEmbeddingService>();
        services.AddScoped<IEmbeddingProvider>(provider => provider.GetRequiredService<BedrockEmbeddingService>());
        services.AddScoped<IEmbeddingService>(provider => provider.GetRequiredService<BedrockEmbeddingService>());

        services.AddScoped<BedrockChatCompletionService>();
        services.AddScoped<IChatProvider>(provider => provider.GetRequiredService<BedrockChatCompletionService>());
        services.AddScoped<IChatCompletionService>(provider => provider.GetRequiredService<BedrockChatCompletionService>());

        services.AddScoped<OpenSearchService>();
        services.AddScoped<IVectorStore>(provider => provider.GetRequiredService<OpenSearchService>());
        services.AddScoped<IVectorSearchService>(provider => provider.GetRequiredService<OpenSearchService>());

        services.AddScoped<IUserRoleService, CognitoUserRoleService>();

        // 3. Initialize common infrastructure (core app services and options bindings)
        services.AddInfrastructure(configuration);

        return services;
    }
}
