using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using AwsRagChat.Application.Interfaces;
using Polly;
using Polly.Registry;
using System;
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
        try
        {
            var cached = await _resiliencePipeline.ExecuteAsync(
                async token => await _cache.GetStringAsync(key, token));

            if (string.IsNullOrWhiteSpace(cached))
                return default;

            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (Exception)
        {
            // Bypassing Redis cache on connection/timeout error
            return default;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration)
    {
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
        }
        catch (Exception)
        {
            // Ignore/bypass Redis cache write errors
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await _resiliencePipeline.ExecuteAsync(
                async token => await _cache.RemoveAsync(key, token));
        }
        catch (Exception)
        {
            // Ignore/bypass Redis cache removal errors
        }
    }
}