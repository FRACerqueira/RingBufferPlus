// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RingBufferPlus;

namespace RingBufferPlusBasicSample
{
    public class Program
    {
        private static IHost? HostApp = null;
        public static async Task Main(string[] args)
        {

            Console.WriteLine("Example of RingBufferPlus - Basic usage with fixed capacity and HeartBeat(10 sec.)");
            Console.WriteLine("==================================================================================");
            Console.WriteLine("");

            HostApp = CreateHostBuilder(args).Build();

            //token to gracefull shutdown
            var tokenapplifetime = HostApp.Services.GetService<IHostApplicationLifetime>()!.ApplicationStopping;

            Random rnd = new();

            var rb = await RingBuffer<int>.New("MyBuffer")
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .HeartBeat(MyHeartBeat)
                .FixedCapacity(3)
                .BuildWarmupAsync(tokenapplifetime);

            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Press any key to start 2 Acquire buffer");
            Console.ReadKey();
            await using (var buffer1 = await rb.AcquireAsync(tokenapplifetime))
            {
                await using var buffer2 = await rb.AcquireAsync(tokenapplifetime);
                Console.WriteLine($"Buffer is ok({buffer1.Successful}:{buffer1.ElapsedTime}) value: {buffer1.Current}");
                Console.WriteLine($"Buffer is ok({buffer2.Successful}:{buffer2.ElapsedTime}) value: {buffer2.Current}");
            }

            Console.WriteLine("Press any key to Acquire buffer and invalidate item buffer");
            Console.ReadKey();
            await using (var buffer3 = await rb.AcquireAsync(tokenapplifetime))
            {
                Console.WriteLine($"Buffer is ok({buffer3.Successful}:{buffer3.ElapsedTime}) value: {buffer3.Current}");
                // Discards this item instead of returning it to the pool on dispose - a
                // replacement is created in its place.
                buffer3.Invalidate();
            }

            Console.WriteLine($"Dispose Ring Buffer...");
            await rb.DisposeAsync();
            Console.WriteLine("Ring Buffer disposed.");
        }

        private static void MyHeartBeat(RingBufferValue<int> value)
        {
            //do anything with value ex: health check
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging((hostContext, logbuilder) =>
                {
                    logbuilder
                        .SetMinimumLevel(LogLevel.Debug)
                        .AddFilter("Microsoft", LogLevel.Warning)
                        .AddFilter("System", LogLevel.Warning)
                        .AddConsole();
                });
    }
}
