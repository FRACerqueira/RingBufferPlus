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
        /// Since v6.0.0 (ADR007V03), warmup is no longer a separate opt-in step - an <see cref="IHostedService"/>
        /// is registered alongside the pool and calls <see cref="IRingBufferService{T}.WarmupAsync(CancellationToken)"/>
        /// automatically in its own <c>StartAsync</c>, using that call's own token. This replaces the previous
        /// <c>WarmupRingBufferAsync</c> extension (removed) and its two root-cause bugs: the caller-supplied
        /// token it silently ignored in one path, and a null-check that could never actually fire.
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

            serviceCollection.AddSingleton((service) =>
            {
                var loggerFactory = service.GetService<ILoggerFactory>();
                return userfunc.Invoke(new RingBufferBuilder<T>(buffername, loggerFactory), service);
            });
            serviceCollection.AddHostedService(service => new RingBufferWarmupHostedService<T>(service, buffername));
            return serviceCollection;
        }
    }

    // Replaces WarmupRingBufferAsync (ADR007V03): registered once per AddRingBuffer<T> call, so a
    // host with several buffers of the same T gets one of these per buffername, each warming up
    // only its own buffer. StartAsync's own token is used directly - no ignored-token bug, and no
    // dead null-check to get wrong, unlike the extension this replaces.
    internal sealed class RingBufferWarmupHostedService<T>(IServiceProvider services, string buffername) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var rb = services.GetServices<IRingBufferService<T>>().FirstOrDefault(x => x.Name == buffername);
            if (rb is null)
            {
                throw new InvalidOperationException($"RingBuffer({buffername}) not found");
            }
            return rb.WarmupAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
