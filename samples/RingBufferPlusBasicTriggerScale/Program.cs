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
                .Capacity(3)
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .AcquireTimeout(TimeSpan.FromMilliseconds(500))
                .ScaleTimer(50, TimeSpan.FromSeconds(5))
                    .AutoScaleAcquireFault(0)
                    .MinCapacity(2)
                    .MaxCapacity(4)
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

            //simulate 3 AcquireAsync to expire free resources
            Console.WriteLine("Try 3 AcquireAsync");
            using (var buffer1 = await rb.AcquireAsync(cts.Token))
            {
                using (var buffer2 = await rb.AcquireAsync(cts.Token))
                {
                    //AcquireAsync fault
                    using (var buffer3 = await rb.AcquireAsync(cts.Token))
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
            //simulate 4 AcquireAsync to expire free resources
            using (var buffer1 = await rb.AcquireAsync(tokenapplifetime))
            {
                using (var buffer2 = await rb.AcquireAsync(tokenapplifetime))
                {
                    //AcquireAsync fault
                    using (var buffer3 = await rb.AcquireAsync(tokenapplifetime))
                    {
                        using (var buffer4 = await rb.AcquireAsync(tokenapplifetime))
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
            cts.Cancel();
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current is {rb.CurrentCapacity}");
            }
            sw.Reset();
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
