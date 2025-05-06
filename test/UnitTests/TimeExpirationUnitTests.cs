using System;
using System.Runtime.CompilerServices;
using System.Threading;
using CodeCargo.NatsDistributedCache.UnitTests.TestHelpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;
using Xunit;

namespace CodeCargo.NatsDistributedCache.UnitTests;

public class TimeExpirationUnitTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection;

    public TimeExpirationUnitTests()
    {
        _mockNatsConnection = new Mock<INatsConnection>();
    }

    private IDistributedCache CreateCacheInstance()
    {
        return new NatsCache(
            Microsoft.Extensions.Options.Options.Create(new NatsCacheOptions
            {
                BucketName = "cache"
            }),
            _mockNatsConnection.Object);
    }

    [Fact]
    public void AbsoluteExpirationInThePastThrows()
    {
        var cache = CreateCacheInstance();
        var key = "AbsoluteExpirationInThePastThrows";
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Fact]
    public void NegativeRelativeExpirationThrows()
    {
        var cache = CreateCacheInstance();
        var key = "NegativeRelativeExpirationThrows";
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public void ZeroRelativeExpirationThrows()
    {
        var cache = CreateCacheInstance();
        var key = "ZeroRelativeExpirationThrows";
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Fact]
    public void NegativeSlidingExpirationThrows()
    {
        var cache = CreateCacheInstance();
        var key = "NegativeSlidingExpirationThrows";
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public void ZeroSlidingExpirationThrows()
    {
        var cache = CreateCacheInstance();
        var key = "ZeroSlidingExpirationThrows";
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.Zero);
    }
}
