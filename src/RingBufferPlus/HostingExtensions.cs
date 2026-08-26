// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RingBufferPlus;
using RingBufferPlus.Core;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Microsoft.Extensions.DependencyInjection
#pragma warning restore IDE0130 // Namespace does not match folder structure
{
    /// <summary>
    /// Represents the commands to add RingBufferPlus in ServiceCollection and Warmup.
    /// </summary>
    public static class HostingExtensions
    {
        /// <summary>
        /// Add RingBuffer in ServiceCollection, warming it up automatically once the host starts.
        /// </summary>
        /// <remarks>
        /// Warmup is no longer a separate opt-in step - an <see cref="IHostedService"/>
        /// is registered alongside the pool and calls <see cref="IRingBufferService{T}.WarmupAsync(CancellationToken)"/>
        /// automatically in its own <c>StartAsync</c>, using that call's own token.
        /// </remarks>
        /// <typeparam name="T">Type of buffer.</typeparam>
        /// <param name="serviceCollection">The <see cref="IServiceCollection"/>.</param>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="userfunc">The Handler to return the <see cref="IRingBufferService{T}"/>.</param>
        /// <returns><see cref="IServiceCollection"/>.</returns>
        /// <exception cref="ArgumentNullException">Buffer name is null. An empty string is accepted.</exception>
        public static IServiceCollection AddRingBuffer<T>(this IServiceCollection serviceCollection, string buffername, Func<IRingBufferBuilder<T>, IServiceProvider, IRingBufferService<T>> userfunc)
        {
            ArgumentNullException.ThrowIfNull(buffername);

            // Registered under a DI key (buffername) so the hosted service below resolves exactly
            // this buffer, without touching any other AddRingBuffer<T> registration of the same T.
            // A broken userfunc for buffer "B" must not fault buffer "A"'s startup.
            //
            // The plain (unkeyed) singleton right after this one only forwards to the same keyed
            // instance - it exists to preserve the documented IEnumerable<IRingBufferService<T>>/
            // "last one wins" constructor-injection behavior (see usage-dependency-injection.md),
            // not to build a second, divergent instance.
            serviceCollection.AddKeyedSingleton(buffername, (service, _) =>
            {
                var loggerFactory = service.GetService<ILoggerFactory>();
                return userfunc.Invoke(new RingBufferBuilder<T>(buffername, loggerFactory), service);
            });
            serviceCollection.AddSingleton(service => service.GetRequiredKeyedService<IRingBufferService<T>>(buffername));
            // AddHostedService<T>(factory) registers via TryAddEnumerable, which dedups by
            // (ServiceType, ImplementationType). RingBufferWarmupHostedService<T> is the same
            // closed generic type for every AddRingBuffer<T> call sharing this T, regardless of
            // buffername - so a second or third call for the same T would silently register zero
            // IHostedService entries (no exception, no log), and only the first buffer of that T
            // would get its automatic warmup.
            //
            // AddSingleton<IHostedService> is additive instead - not deduped by type - so each
            // call genuinely registers its own hosted service instance.
            serviceCollection.AddSingleton<IHostedService>(service => new RingBufferWarmupHostedService<T>(service, buffername));
            return serviceCollection;
        }
    }

    // Registered once per AddRingBuffer<T> call (ADR007V03), so a host with several buffers of
    // the same T gets one of these per buffername, each warming up only its own buffer.
    // StartAsync's own token is used directly.
    internal sealed class RingBufferWarmupHostedService<T>(IServiceProvider services, string buffername) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Resolved by DI key, not by enumerating and filtering every IRingBufferService<T> -
            // see AddRingBuffer<T>'s own comment for why (this is what stops one buffer's broken
            // factory from faulting another's startup).
            var rb = services.GetKeyedService<IRingBufferService<T>>(buffername);
            if (rb is null)
            {
                throw new InvalidOperationException($"RingBuffer({buffername}) not found");
            }
            return rb.WarmupAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
