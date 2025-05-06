// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace CodeCargo.NatsDistributedCache
{
    internal sealed class NatsCacheImpl : NatsCache
    {
        private readonly IServiceProvider _services;

        internal override bool IsHybridCacheActive()
            => _services.GetService<HybridCache>() is not null;

        public NatsCacheImpl(IOptions<NatsCacheOptions> optionsAccessor, ILogger<NatsCache> logger, IServiceProvider services, INatsConnection natsConnection)
            : base(optionsAccessor, logger, natsConnection)
        {
            _services = services; // important: do not check for HybridCache here due to dependency - creates a cycle
        }

        public NatsCacheImpl(IOptions<NatsCacheOptions> optionsAccessor, IServiceProvider services, INatsConnection natsConnection)
            : base(optionsAccessor, natsConnection)
        {
            _services = services; // important: do not check for HybridCache here due to dependency - creates a cycle
        }
    }
}
