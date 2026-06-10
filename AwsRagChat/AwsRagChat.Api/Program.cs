using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using AwsRagChat.Infrastructure.Cache;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.CloudProviders;
using Amazon.Extensions.Configuration.SystemsManager;
using Azure.Identity;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;

var builder = WebApplication.CreateBuilder(args);

var cloudProvider = builder.Configuration["CloudProvider"];

if (string.Equals(cloudProvider, "AWS", StringComparison.OrdinalIgnoreCase))
{
    var environment = builder.Environment.EnvironmentName.ToLowerInvariant();
    builder.Configuration.AddSystemsManager(source =>
    {
        source.Path = $"/rag-chat/{environment}";
        source.Optional = true;
        source.ReloadAfter = TimeSpan.FromMinutes(5);
    });
}
else if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
{
    var vaultUriStr = builder.Configuration["AZURE_KEY_VAULT_URI"];
    if (!string.IsNullOrWhiteSpace(vaultUriStr))
    {
        if (Uri.TryCreate(vaultUriStr, UriKind.Absolute, out var vaultUri))
        {
            builder.Configuration.AddAzureKeyVault(vaultUri, new DefaultAzureCredential());
        }
        else
        {
            Console.WriteLine($"WARNING: AZURE_KEY_VAULT_URI is not a valid absolute URI: {vaultUriStr}");
        }
    }
    else
    {
        Console.WriteLine("WARNING: CloudProvider is Azure but AZURE_KEY_VAULT_URI is not configured. Key Vault config provider was not added.");
    }
}

builder.Logging.ClearProviders();
if (string.Equals(cloudProvider, "AWS", StringComparison.OrdinalIgnoreCase))
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
    });
}
else
{
    builder.Logging.AddConsole();
}
builder.Logging.AddDebug();

// Configure OpenTelemetry for tracing and metrics
var otelBuilder = builder.Services.AddOpenTelemetry();

otelBuilder.WithTracing(tracing =>
{
    tracing.AddSource("AwsRagChat")
           .AddAspNetCoreInstrumentation()
           .AddHttpClientInstrumentation();

    if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
    {
        var connString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrEmpty(connString))
        {
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = connString);
        }
    }
    else
    {
        tracing.AddOtlpExporter();
    }
});

otelBuilder.WithMetrics(metrics =>
{
    metrics.AddMeter("AwsRagChat")
           .AddAspNetCoreInstrumentation()
           .AddHttpClientInstrumentation();

    if (string.Equals(cloudProvider, "Azure", StringComparison.OrdinalIgnoreCase))
    {
        var connString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrEmpty(connString))
        {
            metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = connString);
        }
    }
    else
    {
        metrics.AddOtlpExporter();
    }
});


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

builder.Services.AddCloudProviderAuthentication(builder.Configuration);

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

builder.Services.AddCloudProviderInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors("ConfiguredCors");

app.UseMiddleware<AwsRagChat.Api.Middleware.CorrelationIdMiddleware>();

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
