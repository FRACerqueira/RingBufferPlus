using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

namespace RingBufferPlus
{
    public static class HostingExtensions
    {
        public static IServiceProvider WarmupRingBuffer<T>(this IServiceProvider serviceProvider)
        {
            _ = serviceProvider.GetService<IRunningRingBuffer<T>>();
            return serviceProvider;
        }
        public static IServiceCollection AddRingBuffer<T>(this IServiceCollection ServiceCollection, Func<IServiceProvider, ILoggerFactory, IHostApplicationLifetime, IRingBuffer<T>, IRunningRingBuffer<T>> userfunc)
        {
            return ServiceCollection.AddSingleton((service) =>
            {
                var loggerFactory = service.GetService<ILoggerFactory>();
                var applifetime = service.GetService<IHostApplicationLifetime>();
                return userfunc.Invoke(service, loggerFactory, applifetime, RingBuffer<T>.CreateBuffer());
            });
        }
    }
}
