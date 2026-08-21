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
        /// Add RingBuffer in ServiceCollection.
        /// </summary>
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
            return serviceCollection;
        }

        /// <summary>
        /// Warms up with full capacity ready or reaching timeout.
        /// </summary>
        /// <remarks>
        /// It is recommended to use this method in the initialization of the application.
        /// <para>If you do not use this command, the first access to buffer services (<see cref="IRingBufferService{T}"/>) will trigger warmup instead (not recommended).</para>
        /// </remarks>
        /// <typeparam name="T">Type of buffer.</typeparam>
        /// <param name="appbluild">The <see cref="IHost"/>.</param>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="token">The <see cref="CancellationToken"/>. Default value is <see cref="IHostApplicationLifetime.ApplicationStopping"/>.</param>
        /// <exception cref="ArgumentNullException">Buffer name is null, or no buffer with that name and <typeparamref name="T"/> was registered. An empty string is accepted as a name.</exception>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach initial capacity - propagated from the inner <see cref="IRingBufferService{T}.WarmupAsync(CancellationToken)"/> call.</exception>
        public static async Task WarmupRingBufferAsync<T>(this IHost appbluild, string buffername, CancellationToken? token = null)
        {
            ArgumentNullException.ThrowIfNull(buffername);

            var rb = appbluild.Services.GetServices<IRingBufferService<T>>().FirstOrDefault(x => x.Name == buffername);
            if (rb is null)
            {
                throw new ArgumentNullException(nameof(buffername), $"RingBuffer({buffername}) not found");
            }

            CancellationToken effectiveToken;
            if (token is not null)
            {
                effectiveToken = token.Value;
            }
            else
            {
                var applifetime = appbluild.Services.GetService<IHostApplicationLifetime>();
                effectiveToken = applifetime?.ApplicationStopping ?? CancellationToken.None;
            }

            await rb.WarmupAsync(effectiveToken);
        }
    }
}
