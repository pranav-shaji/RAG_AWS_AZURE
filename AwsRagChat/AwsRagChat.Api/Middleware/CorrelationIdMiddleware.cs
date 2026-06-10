using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AwsRagChat.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId;

        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationIdValues) && 
            !string.IsNullOrWhiteSpace(correlationIdValues))
        {
            correlationId = correlationIdValues.ToString();
        }
        else
        {
            // Fallback to checking W3C traceparent or generate new
            var currentTraceId = Activity.Current?.TraceId.ToString();
            correlationId = !string.IsNullOrEmpty(currentTraceId) ? currentTraceId : Guid.NewGuid().ToString();
        }

        // Attach tag to current tracing Activity span
        Activity.Current?.SetTag("correlation.id", correlationId);
        Activity.Current?.SetTag(CorrelationIdHeader, correlationId);

        // Append to response header
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(CorrelationIdHeader))
            {
                context.Response.Headers.Append(CorrelationIdHeader, correlationId);
            }
            return Task.CompletedTask;
        });

        // Set logging scope with CorrelationId
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await _next(context);
        }
    }
}
