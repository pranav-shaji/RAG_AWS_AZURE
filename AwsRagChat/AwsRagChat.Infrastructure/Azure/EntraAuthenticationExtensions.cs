using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using AwsRagChat.Infrastructure.Options;

namespace AwsRagChat.Infrastructure.Azure;

public static class EntraAuthenticationExtensions
{
    public static IServiceCollection AddEntraIdAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var entraIdOptions = new EntraIdOptions();
        configuration.GetSection(EntraIdOptions.SectionName).Bind(entraIdOptions);

        var authority = string.IsNullOrWhiteSpace(entraIdOptions.Authority)
            ? $"https://login.microsoftonline.com/{entraIdOptions.TenantId}/v2.0"
            : entraIdOptions.Authority;

        if (string.IsNullOrWhiteSpace(entraIdOptions.TenantId))
        {
            throw new InvalidOperationException("Microsoft Entra ID TenantId is required.");
        }

        if (string.IsNullOrWhiteSpace(entraIdOptions.ClientId))
        {
            throw new InvalidOperationException("Microsoft Entra ID ClientId is required.");
        }

        // Initialize static claim mapping
        IdentityOptions.GroupsClaimType = "groups";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = entraIdOptions.ClientId,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "preferred_username"
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        if (principal?.Identity is ClaimsIdentity identity)
                        {
                            // Extract groups
                            var userGroupIds = principal.FindAll("groups").Select(c => c.Value).ToList();

                            // Translate group IDs into roles
                            foreach (var mapping in entraIdOptions.GroupMappings)
                            {
                                if (userGroupIds.Contains(mapping.Value))
                                {
                                    identity.AddClaim(new Claim(ClaimTypes.Role, mapping.Key));
                                }
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
                            "Microsoft Entra ID Authentication failed.");

                        return Task.CompletedTask;
                    },

                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("JwtBearer");

                        logger.LogWarning(
                            "Microsoft Entra ID JWT challenge triggered. Error: {Error}, Description: {Description}",
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
