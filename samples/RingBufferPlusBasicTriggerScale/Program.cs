// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RingBufferPlus;

namespace RingBufferPlusBasicTriggerScale
{
    public class Program
    {
        private static IHost? HostApp = null;
        
        #pragma warning disable IDE0063 // Use simple 'using' statement
        public static async Task Main(string[] args)
        {

            Console.WriteLine("Example of RingBufferPlus - Basic usage with trigger scale");
            Console.WriteLine("==========================================================");
            Console.WriteLine("");

            HostApp = CreateHostBuilder(args).Build();

            //token to gracefull shutdown
            var tokenapplifetime = HostApp.Services.GetService<IHostApplicationLifetime>()!.ApplicationStopping;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(tokenapplifetime);

            Random rnd = new();

            var rb = await RingBuffer<int>.New("MyBuffer")
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .AcquireTimeout(TimeSpan.FromMilliseconds(500))
                .ElasticCapacity(2, 4, 3, 50, TimeSpan.FromSeconds(5))
                // The default deadband (3) exceeds the largest possible target-capacity gap in
                // this sample's 2-4 range (2), so the Monitor could never dispatch a scale
                // operation on its own. Lowering it to 1 lets the Monitor actually act; the
                // other tuning parameters are left at their defaults.
                .MonitorTuning(deadband: 1)
                .BuildWarmupAsync(cts.Token);

            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");
            Console.WriteLine($"wait 7 seconds..");
            var sw = new Stopwatch();
            sw.Start();
            while (sw.ElapsedMilliseconds < 7000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current is {rb.CurrentCapacity}");
            }
            sw.Reset();

            // Acquire 3 items at once, using up all the free resources.
            Console.WriteLine("Try 3 AcquireAsync");
            await using (var buffer1 = await rb.AcquireAsync(cts.Token))
            {
                await using (var buffer2 = await rb.AcquireAsync(cts.Token))
                {
                    await using (var buffer3 = await rb.AcquireAsync(cts.Token))
                    {
                        Console.WriteLine($"Buffer is ok({buffer1.Successful}:{buffer1.ElapsedTime}) value: {buffer1.Current}");
                        Console.WriteLine($"Buffer is ok({buffer2.Successful}:{buffer2.ElapsedTime}) value: {buffer2.Current}");
                        Console.WriteLine($"Buffer is ok({buffer3.Successful}:{buffer3.ElapsedTime}) value: {buffer3.Current}");
                    }
                }
            }

            Console.WriteLine($"Ring Buffer Current capacity = {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Try 4 AcquireAsync");
            // Acquire 4 items at once, using up all the free resources.
            await using (var buffer1 = await rb.AcquireAsync(tokenapplifetime))
            {
                await using (var buffer2 = await rb.AcquireAsync(tokenapplifetime))
                {
                    await using (var buffer3 = await rb.AcquireAsync(tokenapplifetime))
                    {
                        // This 4th acquire may time out if the pool has not grown yet.
                        await using (var buffer4 = await rb.AcquireAsync(tokenapplifetime))
                        {
                            Console.WriteLine($"Buffer is ok({buffer1.Successful}:{buffer1.ElapsedTime}) value: {buffer1.Current}");
                            Console.WriteLine($"Buffer is ok({buffer2.Successful}:{buffer2.ElapsedTime}) value: {buffer2.Current}");
                            Console.WriteLine($"Buffer is ok({buffer3.Successful}:{buffer3.ElapsedTime}) value: {buffer3.Current}");
                            Console.WriteLine($"Buffer is ok({buffer4.Successful}:{buffer4.ElapsedTime}) value: {buffer4.Current}");
                        }
                    }
                }
            }

            Console.WriteLine($"Ring Buffer Current capacity = {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine($"wait 15 seconds..");
            sw.Start();
            while (sw.ElapsedMilliseconds < 15000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current is {rb.CurrentCapacity}");
            }
            sw.Reset();

            Console.WriteLine($"Ring Buffer Current capacity = {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine($"Dispose Ring Buffer...");
            await rb.DisposeAsync();
            cts.Cancel();
            cts.Dispose();
        }
        #pragma warning restore IDE0063 // Use simple 'using' statement

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
