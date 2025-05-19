using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace CodeCargo.ReadmeExample;

public class HybridCacheService
{
    private readonly HybridCache _cache;
    private readonly ILogger<HybridCacheService> _logger;

    public HybridCacheService(HybridCache cache, ILogger<HybridCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task Run()
    {
        _logger.LogInformation("------------------------------------------");
        _logger.LogInformation("HybridCache example");

        // Define key to use
        const string key = "hybrid-cache-greeting";

        // CALLBACK: Begin GetOrCreateAsync example
        // Use GetOrCreateAsync to either get the value from cache or create it if not present
        var result = await _cache.GetOrCreateAsync<string>(
            key,
            _ => ValueTask.FromResult("Hello from NATS Hybrid Cache!"),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) });
        _logger.LogInformation("Got/created value from cache: {Result}", result);
        // CALLBACK: End GetOrCreateAsync example

        // CALLBACK: Begin RemoveAsync example
        // Remove the value from cache
        await _cache.RemoveAsync(key);
        _logger.LogInformation("Removed value from cache");
        // CALLBACK: End RemoveAsync example
    }
}
