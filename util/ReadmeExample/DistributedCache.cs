using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CodeCargo.ReadmeExample;

public class DistributedCacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedCacheService> _logger;

    public DistributedCacheService(IDistributedCache cache, ILogger<DistributedCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task Run()
    {
        _logger.LogInformation("------------------------------------------");
        _logger.LogInformation("DistributedCache example");

        // CALLBACK: Begin SetStringAsync example
        // Set a value
        const string cacheKey = "distributed-cache-greeting";
        const string value = "Hello from NATS Distributed Cache!";
        await _cache.SetStringAsync(
            cacheKey,
            value,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        _logger.LogInformation("Set value in cache: {Value}", value);
        // CALLBACK: End SetStringAsync example

        // CALLBACK: Begin GetStringAsync example
        // Retrieve the value
        var retrievedValue = await _cache.GetStringAsync(cacheKey);
        _logger.LogInformation("Retrieved value from cache: {Value}", retrievedValue);
        // CALLBACK: End GetStringAsync example

        // CALLBACK: Begin RemoveAsync example
        // Remove the value
        await _cache.RemoveAsync(cacheKey);
        _logger.LogInformation("Removed value from cache");
        // CALLBACK: End RemoveAsync example
    }
}
