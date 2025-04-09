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

            //token to app control gracefull shutdown
            var cts = new CancellationTokenSource();

            var rb = await RingBuffer<int>.New("MyBuffer")
                .Capacity(6)
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .ScaleTimer()
                    .MinCapacity(3)
                    .MaxCapacity(9)
                .BuildWarmupAsync(cts.Token);

            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Starting Manual scale with non lock");

            Console.WriteLine("Swith to MinCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MinCapacity);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MaxCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");
           
            Console.WriteLine("Swith to initial Capacity");
            await rb.SwitchToAsync(ScaleSwitch.InitCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MaxCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MinCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MinCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to defaut Capacity");
            await rb.SwitchToAsync(ScaleSwitch.InitCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Dispose ring buffer");

            cts.Cancel();

            Console.WriteLine($"Dispose Ring Buffer...");
            cts.Cancel();
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current is {rb.CurrentCapacity}");
            }
            sw.Reset();

            cts.Dispose();

            Console.WriteLine("Starting Manual scale with lock");

            cts = new CancellationTokenSource();

            rb = await RingBuffer<int>.New("MyBuffer")
                .Capacity(6)
                .Logger(HostApp.Services.GetService<ILogger<Program>>())
                .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
                .ScaleTimer()
                    .LockWhenScaling()
                    .MinCapacity(3)
                    .MaxCapacity(9)
                .BuildWarmupAsync(cts.Token);

            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MinCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MinCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MaxCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to initial Capacity");
            await rb.SwitchToAsync(ScaleSwitch.InitCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MaxCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to MinCapacity");
            await rb.SwitchToAsync(ScaleSwitch.MinCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Swith to defaut Capacity");
            await rb.SwitchToAsync(ScaleSwitch.InitCapacity);
            sw.Start();
            while (sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer Current capacity switch to {rb.CurrentCapacity}");
            }
            sw.Reset();
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine("Dispose ring buffer");

            cts.Cancel();

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
