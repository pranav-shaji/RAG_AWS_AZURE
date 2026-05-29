using Amazon;
using Amazon.BedrockRuntime;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using AwsRagChat.Infrastructure;
using AwsRagChat.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using AwsRagChat.Infrastructure.Cache;
using AwsRagChat.Application.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var region = builder.Configuration["AWS:Region"];
var cognitoAuthority = builder.Configuration["Cognito:Authority"];
var cognitoAppClientId = builder.Configuration["Cognito:AppClientId"];

if (string.IsNullOrWhiteSpace(region))
    throw new InvalidOperationException("AWS:Region is required.");

if (string.IsNullOrWhiteSpace(cognitoAuthority))
    throw new InvalidOperationException("Cognito:Authority is required.");

if (string.IsNullOrWhiteSpace(cognitoAppClientId))
    throw new InvalidOperationException("Cognito:AppClientId is required.");

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:4200"];

var swaggerEnabled = builder.Configuration.GetValue<bool>("Swagger:Enabled");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;

    // Required for reverse-proxy/container environments like App Runner.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("ConfiguredCors", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cognitoAuthority;

        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,

                ValidateAudience = false,

                ValidateLifetime = true,

                ValidateIssuerSigningKey = true,

                // User identity
                NameClaimType = "sub",

                // IMPORTANT:
                // Maps Cognito groups to ASP.NET roles
                // Enables:
                // [Authorize(Roles = "Admin")]
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

                // Only allow Cognito ACCESS tokens
                var tokenUse =
                    principal.FindFirst("token_use")?.Value;

                if (!string.Equals(
                        tokenUse,
                        "access",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Fail(
                        "Only Cognito access tokens are accepted.");

                    return Task.CompletedTask;
                }

                // Ensure token belongs to configured app client
                var clientId =
                    principal.FindFirst("client_id")?.Value;

                if (!string.Equals(
                        clientId,
                        cognitoAppClientId,
                        StringComparison.Ordinal))
                {
                    context.Fail(
                        "Token client_id does not match the configured app client.");

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

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration =
        builder.Configuration["Redis:ConnectionString"];

    options.InstanceName = "AwsRagChat:";
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var configuration = ConfigurationOptions.Parse(
        builder.Configuration["Redis:ConnectionString"]!,
        true);

    configuration.AbortOnConnectFail = false;

    return ConnectionMultiplexer.Connect(configuration);
});

builder.Services.AddSingleton<IRedisCacheService, RedisCacheService>();

builder.Services.AddAuthorization();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AwsRagChat API",
        Version = "v1"
    });

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",

        Description =
            "Paste a bearer token like: Bearer {your_access_token}",

        In = ParameterLocation.Header,

        Type = SecuritySchemeType.Http,

        Scheme = "bearer",

        BearerFormat = "JWT",

        Reference = new OpenApiReference
        {
            Id = "Bearer",
            Type = ReferenceType.SecurityScheme
        }
    };

    options.AddSecurityDefinition(
        "Bearer",
        securityScheme);

    options.AddSecurityRequirement(
        new OpenApiSecurityRequirement
        {
            {
                securityScheme,
                Array.Empty<string>()
            }
        });
});

builder.Services.AddDefaultAWSOptions(new AWSOptions
{
    Region = RegionEndpoint.GetBySystemName(region)
});

builder.Services.AddAWSService<IAmazonS3>();

builder.Services.AddAWSService<IAmazonDynamoDB>();

builder.Services.AddAWSService<IAmazonBedrockRuntime>();

builder.Services.AddAWSService<IAmazonCognitoIdentityProvider>();

builder.Services.AddSingleton<OpenSearchService>();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors("ConfiguredCors");

if (app.Environment.IsDevelopment() || swaggerEnabled)
{
    app.UseSwagger();

    app.UseSwaggerUI();
}

app.UseAuthentication();

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "AwsRagChat.Api",
    utc = DateTime.UtcNow
}));

app.MapControllers();

app.Run();