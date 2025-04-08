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
        /// <param name="ServiceCollection">The <see cref="IServiceCollection"/>.</param>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="userfunc">The Handler to return the <see cref="IRingBufferService{T}"/>.</param>
        /// <returns><see cref="IServiceCollection"/>.</returns>
        /// <exception cref="ArgumentNullException">Buffer name null or empty</exception>

        public static IServiceCollection AddRingBuffer<T>(this IServiceCollection ServiceCollection, string buffername, Func<IRingBuffer<T>,  IServiceProvider, IRingBufferService<T>> userfunc)
        {
            ArgumentNullException.ThrowIfNull(buffername);

            ServiceCollection.AddSingleton((service) =>
            {
                var loggerFactory = service.GetService<ILoggerFactory>();
                return userfunc.Invoke(new RingBufferBuilder<T>(buffername, loggerFactory), service);
            });
            return ServiceCollection;
        }

        /// <summary>
        /// Warms up with full capacity ready or reaching timeout (default 30 seconds).
        /// </summary>
        /// <remarks>
        /// It is recommended to use this method in the initialization of the application.
        /// <para>If you do not use the 'Warmup Ring Buffer' command, the first access to buffer servives(<see cref="IRingBufferService{T}"/>) will be Warmup (not recommended)</para>
        /// <para>If the time limit is reached, the task will continue on to another internal task until it reaches the defined capacity.</para>
        /// </remarks>
        /// <typeparam name="T">Type of buffer.</typeparam>
        /// <param name="appbluild">The <see cref="IHost"/>.</param>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="token">The <see cref="CancellationToken"/>. Default value is <see cref="IHostApplicationLifetime.ApplicationStopping"/>.</param>
        /// <exception cref="ArgumentNullException">Buffer name null or empty</exception>
        /// <exception cref="ArgumentNullException">Buffer not found</exception>
        public static async Task WarmupRingBufferAsync<T>(this IHost appbluild, string buffername, CancellationToken? token = null)
        {
            ArgumentNullException.ThrowIfNull(buffername);
            var applifetime = appbluild.Services.GetService<IHostApplicationLifetime>();
            var rb = appbluild.Services.GetServices<IRingBufferService<T>>().Where(x => x.Name == buffername).FirstOrDefault();
            if (rb is null)
            {
                ArgumentNullException.ThrowIfNull($"RingBuffer({buffername}) not found");
            }
            else
            {
                if (applifetime != null && token is null)
                {
                    await rb.WarmupAsync(applifetime.ApplicationStopping);
                }
                await rb.WarmupAsync(applifetime?.ApplicationStopping ?? CancellationToken.None);
            }
        }
    }
}
