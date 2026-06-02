using Amazon;
using Amazon.BedrockRuntime;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using AwsRagChat.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddDefaultAWSOptions(new AWSOptions
        {
            Region = RegionEndpoint.GetBySystemName(awsRegion)
        });

        services.AddAWSService<IAmazonS3>();
        services.AddAWSService<IAmazonDynamoDB>();
        services.AddAWSService<IAmazonBedrockRuntime>();
        services.AddAWSService<IAmazonCognitoIdentityProvider>();
        services.AddSingleton<OpenSearchService>();

        services.AddInfrastructure(configuration);

        return services;
    }
}
