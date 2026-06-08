using Microsoft.Extensions.Configuration;
using System;
using System.IO;

namespace AwsRagChat.Ingestion.Aws;

public static class AwsIngestionConfiguration
{
    public static IConfiguration Build()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        var tempConfig = builder.Build();
        var cloudProvider = tempConfig["CloudProvider"];

        if (string.Equals(cloudProvider, "AWS", StringComparison.OrdinalIgnoreCase))
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") 
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") 
                ?? "production";

            var envName = environment.ToLowerInvariant();
            builder.AddSystemsManager(source =>
            {
                source.Path = $"/rag-chat/{envName}";
                source.Optional = true;
                source.ReloadAfter = TimeSpan.FromMinutes(5);
            });
        }

        return builder.Build();
    }
}
