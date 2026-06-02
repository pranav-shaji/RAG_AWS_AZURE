using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace AwsRagChat.Infrastructure.Aws;

public static class AwsAuthenticationExtensions
{
    public static IServiceCollection AddAwsCognitoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var cognitoAuthority = configuration["Cognito:Authority"];
        var cognitoAppClientId = configuration["Cognito:AppClientId"];

        if (string.IsNullOrWhiteSpace(cognitoAuthority))
        {
            throw new InvalidOperationException("Cognito:Authority is required.");
        }

        if (string.IsNullOrWhiteSpace(cognitoAppClientId))
        {
            throw new InvalidOperationException("Cognito:AppClientId is required.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = cognitoAuthority;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                    RoleClaimType = "cognito:groups"
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

                        var tokenUse = principal.FindFirst("token_use")?.Value;
                        if (!string.Equals(tokenUse, "access", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Fail("Only Cognito access tokens are accepted.");
                            return Task.CompletedTask;
                        }

                        var clientId = principal.FindFirst("client_id")?.Value;
                        if (!string.Equals(clientId, cognitoAppClientId, StringComparison.Ordinal))
                        {
                            context.Fail("Token client_id does not match the configured app client.");
                            return Task.CompletedTask;
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
