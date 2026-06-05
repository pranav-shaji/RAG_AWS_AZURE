using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using AwsRagChat.Infrastructure.Options;

namespace AwsRagChat.Infrastructure.Aws;

public static class AwsAuthenticationExtensions
{
    public static IServiceCollection AddAwsCognitoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Identity:Provider"] ?? "AWS";
        var authority = configuration["Identity:Authority"] ?? configuration["Cognito:Authority"];
        var clientId = configuration["Identity:ClientId"] ?? configuration["Cognito:AppClientId"];
        var groupsClaim = configuration["Identity:GroupsClaim"] ?? "cognito:groups";

        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException("Identity authority is required.");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Identity client ID is required.");
        }

        // Initialize static claim mapping
        IdentityOptions.GroupsClaimType = groupsClaim;

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                    RoleClaimType = groupsClaim
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;

                        if (principal is null)
                        {
                            context.Fail("No principal found.");
                            return Task.CompletedTask;
                        }

                        // Run strict Cognito validation only if the provider is AWS
                        if (string.Equals(provider, "AWS", StringComparison.OrdinalIgnoreCase))
                        {
                            var tokenUse = principal.FindFirst("token_use")?.Value;
                            if (!string.Equals(tokenUse, "access", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Fail("Only Cognito access tokens are accepted.");
                                return Task.CompletedTask;
                            }

                            var tokenClientId = principal.FindFirst("client_id")?.Value;
                            if (!string.Equals(tokenClientId, clientId, StringComparison.Ordinal))
                            {
                                context.Fail("Token client_id does not match the configured app client.");
                                return Task.CompletedTask;
                            }
                        }

                        return Task.CompletedTask;
                    },

                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("JwtBearer");

                        logger.LogWarning(
                            context.Exception,
                            "Authentication failed.");

                        return Task.CompletedTask;
                    },

                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("JwtBearer");

                        logger.LogWarning(
                            "JWT challenge triggered. Error: {Error}, Description: {Description}",
                            context.Error,
                            context.ErrorDescription);

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }
}
