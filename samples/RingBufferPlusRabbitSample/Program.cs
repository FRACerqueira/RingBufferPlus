// ***************************************************************************************
// Current source code : The maintenance and evolution is maintained by the RingBufferPlus project 
// ***************************************************************************************

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RingBufferPlus;

namespace RingBufferPlusRabbitSample
{
    public class Program
    {
        private const int threadCount = 20;

        private static IHost? hostApp = null;
        private static ConnectionFactory? connectionFactory;
        private static IConnection? connectionRabbit;
        private static readonly Random random = new();
        private static readonly byte[] messageBodyBytes = Encoding.UTF8.GetBytes(RandomString(5000));
        private static readonly List<Thread> threads = [];


        public static async Task Main(string[] args)
        {

            Console.WriteLine("Example of RingBufferPlus - with RabbitMQ");
            Console.WriteLine("=========================================");
            Console.WriteLine("");

            hostApp = CreateHostBuilder(args).Build();

            //token to gracefull shutdown
            var tokenapplifetime = hostApp.Services.GetService<IHostApplicationLifetime>()!.ApplicationStopping;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(tokenapplifetime);

            //Function to create a channel
            static async Task<IChannel> ChannelFactory(CancellationToken cancellation)
            {
                return await connectionRabbit!.CreateChannelAsync(cancellationToken: cancellation);
            }

            //connetion factory to RabbitMQ
            connectionFactory = new ConnectionFactory()
            {
                Port = 8087,
                HostName = "localhost",
                UserName = "guest",
                Password = "guest",
                ClientProvidedName = "PublisherRoleProgram"
            };

            //create queue
            var argsqueue = new Dictionary<string, object>
            {
                { "x-message-ttl", 1000 }
            };
#pragma warning disable IDE0063 // Use simple 'using' statement
            using (var cnn = await connectionFactory.CreateConnectionAsync(cts.Token))
            {
                using (var chn = await cnn.CreateChannelAsync(cancellationToken: cts.Token))
                {
                    await chn.QueueDeclareAsync("log", false, false, false, argsqueue!, cancellationToken: cts.Token);
                }
            }
#pragma warning restore IDE0063 // Use simple 'using' statement

            //create connection
            connectionRabbit = await connectionFactory!.CreateConnectionAsync(cts.Token);

            //create ring buffer    
            var rb = await RingBuffer<IChannel>.New("RabbitChanels")
                .Capacity(10)
                .Logger(hostApp.Services.GetService<ILogger<Program>>())
                .BackgroundLogger()
                .Factory((cts) => ChannelFactory(cts)!)
                .ScaleTimer(50, TimeSpan.FromSeconds(5))
                    .MaxCapacity(20)
                    .MinCapacity(5)
                    .AutoScaleAcquireFault()
                .BuildWarmupAsync(cts.Token);

            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity = {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

            Console.WriteLine($"Wait... 20 sec. to start {threadCount} thread");
            Thread.Sleep(TimeSpan.FromSeconds(20));

            Console.WriteLine($"Running 60 seconds..");
            Thread.Sleep(TimeSpan.FromSeconds(1));

            var dtref = DateTime.Now.AddSeconds(60);
            var qtdstart = 0;
            for (int i = 0; i < threadCount; i++)
            {
                Thread thread = new(async () =>
                {
                    var id = Interlocked.Increment(ref qtdstart);
                    Console.WriteLine($"Thread {qtdstart} started ");
                    while (true)
                    {
                        if (DateTime.Now >= dtref)
                        {
                            Console.WriteLine($"wait({id}) 60 seconds (idle)");
                            Thread.Sleep(TimeSpan.FromSeconds(60));
                            break;
                        }
                        using var bufferedItem = await rb!.AcquireAsync();
                        if (bufferedItem.Successful)
                        {
                            var body = new ReadOnlyMemory<byte>(messageBodyBytes);
                            await bufferedItem.Current!.BasicPublishAsync("", "log", body);
                        }
                        else
                        {
                            if (!cts.IsCancellationRequested)
                            {
                                Console.WriteLine($"RingBuffer-{id}({bufferedItem.Successful}:{bufferedItem.ElapsedTime}) Channel Capacity({rb!.CurrentCapacity})");
                            }
                        }
                    }
                    Console.WriteLine($"Thread {id} ended");
                    Interlocked.Decrement(ref qtdstart);
                });
                thread.Start();
                threads.Add(thread);
            }

            Console.WriteLine($"Waiting for {threadCount} threads to finish...");
            while (qtdstart > 0)
            {
                Thread.Sleep(10);
            }

            Console.WriteLine("Dispose ring buffer");
            cts.Cancel();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ring Buffer {rb!.Name} current capacity : {rb!.CurrentCapacity}");
            }
            sw.Reset();

        }

        public static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
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
