// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RingBufferPlus;

namespace Microsoft.Extensions.DependencyInjection
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
        public static IServiceCollection AddRingBuffer<T>(this IServiceCollection ServiceCollection,string buffername,  Func<IRingBuffer<T>, IServiceProvider, IRingBufferService<T>> userfunc) 
        {
#if NETSTANDARD2_1
            if (buffername is null)
            {
                throw new ArgumentNullException(nameof(buffername));
            }
#else
            ArgumentNullException.ThrowIfNull(buffername);

#endif
            ServiceCollection.AddSingleton((service) =>
            {
                var loggerFactory = service.GetService<ILoggerFactory>();
                var applifetime = service.GetService<IHostApplicationLifetime>();
                return userfunc.Invoke(new RingBufferBuilder<T>(buffername, loggerFactory, applifetime.ApplicationStopping), service);
            });
            return ServiceCollection;
        }

        /// <summary>
        /// Warmup RingBuffer  with full capacity ready or reaching timeout .
        /// <br>If you do not use the 'Warmup Ring Buffer' command, the first access to acquire the buffer will be Warmup (not recommended)</br>
        /// </summary>
        /// <typeparam name="T">Type of buffer.</typeparam>
        /// <param name="appbluild">The <see cref="IHost"/>.</param>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="timeout">The timeout for full capacity ready. Default value is 30 seconds.</param>
        /// <returns>True if full capacity ready, otherwise false (Timeout but keeps running).</returns>
        public static bool WarmupRingBuffer<T>(this IHost appbluild, string buffername, TimeSpan? timeout = null)
        {
            var rb = (IRingBufferWarmup<T>)appbluild.Services.GetServices<IRingBufferService<T>>().Where(x => x.Name == buffername).First();
            return rb.Warmup(timeout);
        }
    }
}
