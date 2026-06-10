using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using System.Diagnostics;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Telemetry;
using Polly;
using Polly.Registry;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AwsRagChat.Infrastructure.Cache;

public class RedisCacheService : IRedisCacheService
{
    private readonly IDistributedCache _cache;
    private readonly ResiliencePipeline _resiliencePipeline;

    public RedisCacheService(
        IDistributedCache cache,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _cache = cache;
        _resiliencePipeline = pipelineProvider.GetPipeline("RedisPipeline");
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        using var activity = ApplicationTelemetry.Source.StartActivity(
            "RedisCache.Get",
            ActivityKind.Internal);
        activity?.SetTag("cache.key", key);

        try
        {
            var cached = await _resiliencePipeline.ExecuteAsync(
                async token => await _cache.GetStringAsync(key, token));

            if (string.IsNullOrWhiteSpace(cached))
            {
                ApplicationTelemetry.CacheMissCounter.Add(1, new KeyValuePair<string, object?>("operation", "Get"));
                activity?.SetTag("cache.result", "miss");
                return default;
            }

            ApplicationTelemetry.CacheHitCounter.Add(1, new KeyValuePair<string, object?>("operation", "Get"));
            activity?.SetTag("cache.result", "hit");
            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (Exception ex)
        {
            // Bypassing Redis cache on connection/timeout error
            ApplicationTelemetry.CacheMissCounter.Add(1, 
                new KeyValuePair<string, object?>("operation", "Get"),
                new KeyValuePair<string, object?>("error", ex.GetType().Name));
            activity?.SetTag("cache.result", "error");
            activity?.SetTag("error", ex.Message);
            return default;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration)
    {
        using var activity = ApplicationTelemetry.Source.StartActivity(
            "RedisCache.Set",
            ActivityKind.Internal);
        activity?.SetTag("cache.key", key);

        try
        {
            var json = JsonSerializer.Serialize(value);

            await _resiliencePipeline.ExecuteAsync(
                async token => await _cache.SetStringAsync(
                    key,
                    json,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = expiration
                    },
                    token));
            
            activity?.SetTag("cache.result", "success");
        }
        catch (Exception ex)
        {
            // Ignore/bypass Redis cache write errors
            activity?.SetTag("cache.result", "error");
            activity?.SetTag("error", ex.Message);
        }
    }

    public async Task RemoveAsync(string key)
    {
        using var activity = ApplicationTelemetry.Source.StartActivity(
            "RedisCache.Remove",
            ActivityKind.Internal);
        activity?.SetTag("cache.key", key);

        try
        {
            await _resiliencePipeline.ExecuteAsync(
                async token => await _cache.RemoveAsync(key, token));
            
            activity?.SetTag("cache.result", "success");
        }
        catch (Exception ex)
        {
            // Ignore/bypass Redis cache removal errors
            activity?.SetTag("cache.result", "error");
            activity?.SetTag("error", ex.Message);
        }
    }
}