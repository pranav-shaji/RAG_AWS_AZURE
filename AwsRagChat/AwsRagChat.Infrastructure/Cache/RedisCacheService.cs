using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using AwsRagChat.Application.Interfaces;

namespace AwsRagChat.Infrastructure.Cache;

public class RedisCacheService : IRedisCacheService
{
    private readonly IDistributedCache _cache;

    public RedisCacheService(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        var cached =
            await _cache.GetStringAsync(key);

        if (string.IsNullOrWhiteSpace(cached))
            return default;

        return JsonSerializer.Deserialize<T>(cached);
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration)
    {
        var json =
            JsonSerializer.Serialize(value);

        await _cache.SetStringAsync(
            key,
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            });
    }

    public async Task RemoveAsync(string key)
    {
        await _cache.RemoveAsync(key);
    }
}