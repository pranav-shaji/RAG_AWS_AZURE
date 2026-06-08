using System;
using AwsRagChat.Infrastructure.Aws;
using AwsRagChat.Infrastructure.Azure;
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

        if (string.Equals(cloudProvider, CloudProviderNames.Azure, StringComparison.OrdinalIgnoreCase))
        {
            return services.AddEntraIdAuthentication(configuration);
        }

        throw new InvalidOperationException($"Unsupported CloudProvider: '{cloudProvider}'.");
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

        if (string.Equals(cloudProvider, CloudProviderNames.Azure, StringComparison.OrdinalIgnoreCase))
        {
            return services.AddAzureProviderInfrastructure(configuration);
        }

        throw new InvalidOperationException($"Unsupported CloudProvider: '{cloudProvider}'.");
    }

    private static string GetCloudProvider(IConfiguration configuration)
    {
        var cloudProvider = configuration["CloudProvider"];

        if (string.IsNullOrWhiteSpace(cloudProvider))
        {
            throw new InvalidOperationException("CloudProvider must be explicitly configured in settings. Valid values are 'AWS' or 'Azure'.");
        }

        if (string.Equals(cloudProvider, CloudProviderNames.Aws, StringComparison.OrdinalIgnoreCase))
        {
            return CloudProviderNames.Aws;
        }

        if (string.Equals(cloudProvider, CloudProviderNames.Azure, StringComparison.OrdinalIgnoreCase))
        {
            return CloudProviderNames.Azure;
        }

        throw new InvalidOperationException($"CloudProvider value '{cloudProvider}' is invalid. Explicitly configure CloudProvider to 'AWS' or 'Azure'.");
    }
}
