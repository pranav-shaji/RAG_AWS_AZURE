using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using AwsRagChat.Infrastructure.Cache;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.CloudProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

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
