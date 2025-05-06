// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace CodeCargo.NatsDistributedCache
{
    /// <summary>
    /// Extension methods for setting up NATS distributed cache related services in an <see cref="IServiceCollection" />.
    /// </summary>
    public static class NatsCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds NATS distributed caching services to the specified <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
        /// <param name="setupAction">An <see cref="Action{NatsCacheOptions}"/> to configure the provided
        /// <see cref="NatsCacheOptions"/>.</param>
        /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        public static IServiceCollection AddNatsDistributedCache(this IServiceCollection services, Action<NatsCacheOptions> setupAction)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (setupAction == null)
            {
                throw new ArgumentNullException(nameof(setupAction));
            }

            services.AddOptions();
            services.Configure(setupAction);
            services.Add(ServiceDescriptor.Singleton<IDistributedCache, NatsCacheImpl>(serviceProvider =>
            {
                var optionsAccessor = serviceProvider.GetRequiredService<IOptions<NatsCacheOptions>>();
                var logger = serviceProvider.GetService<ILogger<NatsCache>>();
                var natsConnection = serviceProvider.GetRequiredService<INatsConnection>();

                if (logger != null)
                {
                    return new NatsCacheImpl(optionsAccessor, logger, serviceProvider, natsConnection);
                }

                return new NatsCacheImpl(optionsAccessor, serviceProvider, natsConnection);
            }));

            return services;
        }
    }
}
