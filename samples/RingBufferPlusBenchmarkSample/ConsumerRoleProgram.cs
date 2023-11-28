// ***************************************************************************************
// Original source code : Copyright 2020 Luis Carlos Farias.
// https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer
// Current source code : The maintenance and evolution is maintained by the RingBufferPlus project 
// ***************************************************************************************

using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RingBufferPlus;

namespace RingBufferPlusBenchmarkSample
{
    internal class ConsumerRoleProgram
    {
        private static ConnectionFactory? ConnectionFactory;
        private static IRingBufferService<IModel>? modelRingBuffer;
        private static IRingBufferService<IConnection>? connectionRingBuffer;
        private static bool completedCnn;
        private static bool completedChanels;
        private static ILogger? applogger;

        static IModel? ModelFactory(CancellationToken cancellation)
        {
            IModel? model = null;
            while (!cancellation.IsCancellationRequested)
            {
                using var connectionWrapper = connectionRingBuffer!.Accquire();
                if (connectionWrapper.Successful && connectionWrapper.Current.IsOpen)
                {
                    model = connectionWrapper.Current.CreateModel();
                    model.QueueDeclare("log", false, false, false);
                    break;
                }
                cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            }
            return model;
        }


        private static void Init()
        {
            ConnectionFactory = new ConnectionFactory()
            {
                Port = 8087,
                HostName = "localhost",
                UserName = "guest",
                Password = "guest",
                VirtualHost = "EnterpriseLog",
                AutomaticRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromMinutes(1),
                ClientProvidedName = "ConsumerRoleProgram"
            };

            connectionRingBuffer = RingBuffer<IConnection>.New("RabbitCnn")
                .Capacity(5)
                .Logger(applogger!)
                .OnError((log, error) =>
                {
                    log?.LogError("{error}", error);
                })
                .Factory((cts) => ConnectionFactory.CreateConnection())
                .AccquireTimeout(TimeSpan.FromMilliseconds(1500))
                .BuildWarmup(out completedCnn);

            modelRingBuffer = RingBuffer<IModel>.New("RabbitChanels")
                .Capacity(20)
                .Logger(applogger!)
                .OnError((log, error) =>
                {
                    log?.LogError("{error}", error);
                })
                .Factory((cts) => ModelFactory(cts)!)
                .AccquireTimeout(TimeSpan.FromMilliseconds(1500))
                .BuildWarmup(out completedChanels);
        }


        public static void Start(ILogger logger, int delaysec)
        {
            applogger = logger;


            Console.WriteLine("Initializing...");

            Init();


            byte[] messageBodyBytes = Encoding.UTF8.GetBytes(".");

            #region connectionRingBuffer Show Properties

            Console.WriteLine($"Ring Buffer {connectionRingBuffer!.Name}");
            Console.WriteLine($"Ring Buffer Warmup({completedCnn})");
            Console.WriteLine($"Ring Buffer Capacity({connectionRingBuffer!.Capacity})");
            Console.WriteLine($"Ring Buffer MinCapacity({connectionRingBuffer!.MinCapacity})");
            Console.WriteLine($"Ring Buffer MaxCapacity({connectionRingBuffer!.MaxCapacity})");
            Console.WriteLine($"Ring Buffer ScaleCapacity({connectionRingBuffer!.ScaleCapacity})");
            Console.WriteLine($"Ring Buffer AccquireTimeout({connectionRingBuffer!.AccquireTimeout})");
            Console.WriteLine($"Ring Buffer FactoryTimeout({connectionRingBuffer!.FactoryTimeout})");
            Console.WriteLine($"Ring Buffer FactoryIdleRetry({connectionRingBuffer!.FactoryIdleRetry})");
            Console.WriteLine($"Ring Buffer SampleUnit({connectionRingBuffer!.SampleUnit})");
            Console.WriteLine($"Ring Buffer SamplesCount({connectionRingBuffer!.SamplesCount})");
            Console.WriteLine($"Ring Buffer ScaleToMin({connectionRingBuffer!.ScaleToMin})");
            Console.WriteLine($"Ring Buffer RollbackFromMin({connectionRingBuffer!.RollbackFromMin})");
            Console.WriteLine($"Ring Buffer TriggerFromMin({connectionRingBuffer!.TriggerFromMin})");
            Console.WriteLine($"Ring Buffer ScaleToMax({connectionRingBuffer!.ScaleToMax})");
            Console.WriteLine($"Ring Buffer RollbackFromMax({connectionRingBuffer!.RollbackFromMax})");
            Console.WriteLine($"Ring Buffer TriggerFromMax({connectionRingBuffer!.TriggerFromMax})");

            Console.WriteLine();

            #endregion

            #region modelRingBuffer Show Properties


            Console.WriteLine($"Ring Buffer {modelRingBuffer!.Name}");
            Console.WriteLine($"Ring Buffer Warmup({completedChanels})");
            Console.WriteLine($"Ring Buffer Capacity({modelRingBuffer.Capacity})");
            Console.WriteLine($"Ring Buffer MinCapacity({modelRingBuffer.MinCapacity})");
            Console.WriteLine($"Ring Buffer MaxCapacity({modelRingBuffer.MaxCapacity})");
            Console.WriteLine($"Ring Buffer ScaleCapacity({modelRingBuffer.ScaleCapacity})");
            Console.WriteLine($"Ring Buffer AccquireTimeout({modelRingBuffer.AccquireTimeout})");
            Console.WriteLine($"Ring Buffer FactoryTimeout({modelRingBuffer.FactoryTimeout})");
            Console.WriteLine($"Ring Buffer FactoryIdleRetry({modelRingBuffer.FactoryIdleRetry})");
            Console.WriteLine($"Ring Buffer SampleUnit({modelRingBuffer.SampleUnit})");
            Console.WriteLine($"Ring Buffer SamplesCount({modelRingBuffer.SamplesCount})");
            Console.WriteLine($"Ring Buffer ScaleToMin({modelRingBuffer.ScaleToMin})");
            Console.WriteLine($"Ring Buffer RollbackFromMin({modelRingBuffer.RollbackFromMin})");
            Console.WriteLine($"Ring Buffer TriggerFromMin({modelRingBuffer.TriggerFromMin})");
            Console.WriteLine($"Ring Buffer ScaleToMax({modelRingBuffer.ScaleToMax})");
            Console.WriteLine($"Ring Buffer RollbackFromMax({modelRingBuffer.RollbackFromMax})");
            Console.WriteLine($"Ring Buffer TriggerFromMax({modelRingBuffer.TriggerFromMax})");

            Console.WriteLine();

            #endregion

            Console.WriteLine($"Wait... {delaysec}sec. to start {modelRingBuffer.Capacity} Consumers"); 
            Thread.Sleep(TimeSpan.FromSeconds(delaysec));
            Console.WriteLine($"Running");

            var i = 0;
            while (i < modelRingBuffer.Capacity)
            {
                var bufferedItem = modelRingBuffer.Accquire();
                if (bufferedItem.Successful)
                {
                    try
                    {
                        var consumer = new AsyncEventingBasicConsumer(bufferedItem.Current);
                        consumer.Received += async (ch, ea) =>
                        {
                            await Task.Yield();
                        };
                        bufferedItem.Current.BasicConsume("log", true, consumer);
                        i++;
                        Console.WriteLine($"{modelRingBuffer.Name} consumers({i}) listening");

                    }
                    catch (Exception)
                    {
                        Console.WriteLine($"{modelRingBuffer.Name} buffer is invalid!");
                        bufferedItem.Invalidate();
                    }
                }
                else
                {
                    //do something! no buffer available
                }
            }

            while (true)
                Console.ReadLine();

        }
    }
}
