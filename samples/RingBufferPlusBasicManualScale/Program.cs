// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RingBufferPlus;

namespace RingBufferPlusBasicManualScale
{
    public class Program
    {
        private static IHost? HostApp = null;
        public static async Task Main(string[] args)
        {

            Console.WriteLine("Example of RingBufferPlus - Basic usage with Manual scale");
            Console.WriteLine("=========================================================");
            Console.WriteLine("");

            HostApp = CreateHostBuilder(args).Build();

            Random rnd = new();

            // Token used to control graceful shutdown.
            var cts = new CancellationTokenSource();

            var rb = await RingBuffer<int>.New("MyBuffer")
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .ElasticCapacity(3, 9, 6)
                .BuildWarmupAsync(cts.Token);

            ReportCreated(rb);

            Console.WriteLine("Starting Manual scale with non lock");

            await DemoSwitchAsync(rb, ScaleSwitch.MinCapacity, "MinCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MaxCapacity, "MaxCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.InitCapacity, "initial capacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MaxCapacity, "MaxCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MinCapacity, "MinCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.InitCapacity, "default capacity");

            Console.WriteLine("Dispose ring buffer");
            await rb.DisposeAsync();
            cts.Cancel();
            cts.Dispose();

            Console.WriteLine("Starting Manual scale with lock");

            cts = new CancellationTokenSource();

            rb = await RingBuffer<int>.New("MyBuffer")
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .ElasticCapacity(3, 9, 6)
                .LockWhenScaling()
                .BuildWarmupAsync(cts.Token);

            ReportCreated(rb);

            await DemoSwitchAsync(rb, ScaleSwitch.MinCapacity, "MinCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MaxCapacity, "MaxCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.InitCapacity, "initial capacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MaxCapacity, "MaxCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.MinCapacity, "MinCapacity");
            await DemoSwitchAsync(rb, ScaleSwitch.InitCapacity, "default capacity");

            Console.WriteLine("Dispose ring buffer");
            await rb.DisposeAsync();
            cts.Cancel();
            cts.Dispose();
        }

        // Switches capacity, then polls for 5 seconds so you can watch it move. With
        // LockWhenScaling(), SwitchToAsync already waits for the change - the poll loop still
        // shows the settled value either way.
        private static async Task DemoSwitchAsync(IRingBufferManualScaleService<int> rb, ScaleSwitch target, string label)
        {
            Console.WriteLine($"Switch to {label}");
            await rb.SwitchToAsync(target, TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity is {rb.CurrentCapacity}");
            }
            ReportState(rb);
        }

        private static void ReportCreated(IRingBufferManualScaleService<int> rb)
        {
            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
            ReportState(rb);
        }

        private static void ReportState(IRingBufferManualScaleService<int> rb)
        {
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");
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
