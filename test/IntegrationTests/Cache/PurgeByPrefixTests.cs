using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class PurgeByPrefixTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    // The maintenance surface and IDistributedCache resolve to the same NatsCache singleton, so seeding via
    // DistributedCache and purging via Maintenance operate on one bucket, key prefix, and key encoder.
    private INatsCacheMaintenance Maintenance => ServiceProvider.GetRequiredService<INatsCacheMaintenance>();

    [Fact]
    public async Task PurgeByPrefixRemovesMatchingKeysAndLeavesOthers()
    {
        var token = TestContext.Current.CancellationToken;
        var value = new byte[] { 1 };

        string[] aKeys = ["A.one", "A.two", "A.three"];
        string[] bKeys = ["B.one", "B.two"];

        foreach (var key in aKeys.Concat(bKeys))
        {
            await DistributedCache.SetAsync(key, value, token);
        }

        var purged = await Maintenance.PurgeByPrefixAsync("A", token);

        Assert.Equal(aKeys.Length, (int)purged);
        foreach (var key in aKeys)
        {
            Assert.Null(await DistributedCache.GetAsync(key, token));
        }

        foreach (var key in bKeys)
        {
            Assert.NotNull(await DistributedCache.GetAsync(key, token));
        }

        // A second purge finds nothing left under the prefix.
        var purgedAgain = await Maintenance.PurgeByPrefixAsync("A", token);
        Assert.Equal(0, (int)purgedAgain);
    }

    [Fact]
    public async Task PurgeByPrefixMatchesKeysThatRequireEncoding()
    {
        // '#' is not a valid unencoded key character, so the encoder escapes it (# -> %23 -> =23). This
        // exercises the encoding nuance the subject filter relies on: the encoder leaves the '.' separator
        // unescaped, so the encoded prefix stays a leading segment of every encoded full key beneath it and
        // NATS subject-wildcard filtering by prefix keeps working.
        var token = TestContext.Current.CancellationToken;
        var value = new byte[] { 1 };

        string[] targetKeys = ["tenant#1.one", "tenant#1.two"];
        const string controlKey = "tenant#2.one"; // shares a stem but a different tenant; must survive

        foreach (var key in targetKeys)
        {
            await DistributedCache.SetAsync(key, value, token);
        }

        await DistributedCache.SetAsync(controlKey, value, token);

        var purged = await Maintenance.PurgeByPrefixAsync("tenant#1", token);

        Assert.Equal(targetKeys.Length, (int)purged);
        foreach (var key in targetKeys)
        {
            Assert.Null(await DistributedCache.GetAsync(key, token));
        }

        Assert.NotNull(await DistributedCache.GetAsync(controlKey, token));
    }
}
