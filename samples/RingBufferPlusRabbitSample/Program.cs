// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

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
        private const int WorkerCount = 20;

        private static IHost? hostApp = null;
        private static ConnectionFactory? connectionFactory;
        private static IConnection? connectionRabbit;
        private static readonly Random random = new();
        private static readonly byte[] messageBodyBytes = Encoding.UTF8.GetBytes(RandomString(5000));

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

            //connection factory to RabbitMQ
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
            await using (var cnn = await connectionFactory.CreateConnectionAsync(cts.Token))
            await using (var chn = await cnn.CreateChannelAsync(cancellationToken: cts.Token))
            {
                await chn.QueueDeclareAsync("log", false, false, false, argsqueue!, cancellationToken: cts.Token);
            }

            //create connection
            connectionRabbit = await connectionFactory!.CreateConnectionAsync(cts.Token);

            //create ring buffer, no lock while scaling
            var rb = await RingBuffer<IChannel>.New("RabbitChanels")
                .Logger(hostApp.Services.GetService<ILogger<Program>>())
                .BackgroundLogger()
                .Factory((token) => ChannelFactory(token)!)
                .ElasticCapacity(10, 5, 20, 50, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault()
                .BuildWarmupAsync(cts.Token);

            ReportCapacity(rb);
            await RunLoadTestAsync(rb, cts.Token);

            Console.WriteLine("Dispose ring buffer");
            await rb.DisposeAsync();
            cts.Cancel();
            cts.Dispose();

            cts = CancellationTokenSource.CreateLinkedTokenSource(tokenapplifetime);

            //create ring buffer again, this time locking acquire/switch while scaling
            rb = await RingBuffer<IChannel>.New("RabbitChanels")
                .Logger(hostApp.Services.GetService<ILogger<Program>>())
                .BackgroundLogger()
                .Factory((token) => ChannelFactory(token)!)
                .ElasticCapacity(10, 5, 20, 50, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .AutoScaleAcquireFault()
                .BuildWarmupAsync(cts.Token);

            ReportCapacity(rb);
            await RunLoadTestAsync(rb, cts.Token);

            Console.WriteLine("Dispose ring buffer");
            await rb.DisposeAsync();
            cts.Cancel();
            cts.Dispose();
        }

        // Publishes from WorkerCount concurrent tasks for 60 seconds, sharing one ring buffer of
        // RabbitMQ channels - this is the pattern channel pooling exists for: many concurrent
        // publishers, one shared IConnection, no per-publish channel-open cost.
        private static async Task RunLoadTestAsync(IRingBufferService<IChannel> rb, CancellationToken shutdownToken)
        {
            Console.WriteLine($"Wait... 20 sec. before starting {WorkerCount} concurrent workers");
            await Task.Delay(TimeSpan.FromSeconds(20), shutdownToken);

            Console.WriteLine("Running for 60 seconds..");
            var deadline = DateTime.Now.AddSeconds(60);

            var workers = Enumerable.Range(1, WorkerCount).Select(id => Task.Run(async () =>
            {
                Console.WriteLine($"Worker {id} started");
                while (DateTime.Now < deadline)
                {
                    await using var bufferedItem = await rb.AcquireAsync(shutdownToken);
                    if (bufferedItem.Successful)
                    {
                        var body = new ReadOnlyMemory<byte>(messageBodyBytes);
                        await bufferedItem.Current!.BasicPublishAsync("", "log", body);
                    }
                    else if (!shutdownToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Worker-{id}({bufferedItem.Successful}:{bufferedItem.ElapsedTime}) Channel Capacity({rb.CurrentCapacity})");
                    }
                }
                Console.WriteLine($"Worker {id} ended");
            }, shutdownToken)).ToArray();

            Console.WriteLine($"Waiting for {WorkerCount} workers to finish...");
            await Task.WhenAll(workers);
        }

        private static void ReportCapacity(IRingBufferService<IChannel> rb)
        {
            Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
            Console.WriteLine($"Ring Buffer Current capacity = {rb.CurrentCapacity}");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
            Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");
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
