using AwsRagChat.Infrastructure.Aws;
using AwsRagChat.Infrastructure.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AwsRagChat.Infrastructure.CloudProviders;

public static class CloudProviderServiceCollectionExtensions
{
    public static IServiceCollection AddCloudProviderAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var cloudProvider = GetCloudProvider(configuration);

        if (string.Equals(cloudProvider, CloudProviderNames.Aws, StringComparison.OrdinalIgnoreCase))
        {
            return services.AddAwsCognitoAuthentication(configuration);
        }

        throw UnsupportedProvider(cloudProvider);
    }

    public static IServiceCollection AddCloudProviderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var cloudProvider = GetCloudProvider(configuration);

        if (string.Equals(cloudProvider, CloudProviderNames.Aws, StringComparison.OrdinalIgnoreCase))
        {
            return services.AddAwsProviderInfrastructure(configuration);
        }

        throw UnsupportedProvider(cloudProvider);
    }

    private static string GetCloudProvider(IConfiguration configuration)
    {
        var cloudProvider = configuration["CloudProvider"];

        return string.IsNullOrWhiteSpace(cloudProvider)
            ? CloudProviderNames.Aws
            : cloudProvider;
    }

    private static InvalidOperationException UnsupportedProvider(string cloudProvider)
    {
        return new InvalidOperationException(
            $"CloudProvider '{cloudProvider}' is not supported yet. Only 'AWS' is available in this build.");
    }
}
