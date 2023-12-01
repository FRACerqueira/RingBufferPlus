// ***************************************************************************************
// Original source code : Copyright 2020 Luis Carlos Farias.
// https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer
// Current source code : The maintenance and evolution is maintained by the RingBufferPlus project 
// ***************************************************************************************

using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RingBufferPlus;

namespace RingBufferPlusBenchmarkSample
{
    internal class PublisherRoleProgram 
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
                try
                {
                    if (connectionWrapper.Successful)
                    {
                        model = connectionWrapper.Current.CreateModel();
                        break;
                    }
                }
                catch 
                {
                }
                cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(2));
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
                AutomaticRecoveryEnabled = false,
                RequestedHeartbeat = TimeSpan.FromMinutes(1),
                ClientProvidedName = "PublisherRoleProgram"
            };

            connectionRingBuffer = RingBuffer<IConnection>.New("RabbitCnn")
                .Capacity(2)
                .Logger(applogger!)
                .AccquireTimeout(TimeSpan.FromMilliseconds(500))
                .OnError((log, error) =>
                    {
                        log?.LogError($"{error.NameRingBuffer}: {error.Message}");
                    })
                .Factory((cts) => ConnectionFactory.CreateConnection())
                .SlaveScale()
                    .ReportScale((metric, log, cts) =>
                    {
                        log?.LogInformation($"RabbitCnn Report: [{metric.MetricDate}]  Trigger {metric.Trigger} from {metric.FromCapacity} to {metric.ToCapacity}");
                    })
                    .MaxCapacity(5)
                    .MinCapacity(1)
                .BuildWarmup(out completedCnn);

            modelRingBuffer = RingBuffer<IModel>.New("RabbitChanels")
                .Capacity(10)
                .Logger(applogger!)
                .OnError((log, error) =>
                    {
                        log?.LogError($"{error.NameRingBuffer}: {error.Message}");
                    })
                .Factory((cts) => ModelFactory(cts)!)
                .BufferHealth((buffer) => buffer.IsOpen, TimeSpan.FromSeconds(5))
                .MasterScale(connectionRingBuffer)
                    .SampleUnit(TimeSpan.FromSeconds(10), 50)
                    .ReportScale((metric, log, cts) => 
                    {
                        log?.LogInformation($"RabbitChanels Report: [{metric.MetricDate}]  Trigger {metric.Trigger} from {metric.FromCapacity} to {metric.ToCapacity}");
                    })
                    .MaxCapacity(50)
                        .ScaleWhenFreeLessEq()
                        .RollbackWhenFreeGreaterEq()
                    .MinCapacity(2)
                        .ScaleWhenFreeGreaterEq()
                        .RollbackWhenFreeLessEq()
                .BuildWarmup(out completedChanels);

        }


        public static void Start(ILogger logger,int delaysec)
        {
            applogger = logger;


            Console.WriteLine("Initializing...");

            Init();


            byte[] messageBodyBytes = Encoding.UTF8.GetBytes(".");

            int threadCount = 20;

            List<Thread> threads = [];

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
            Console.WriteLine($"Ring Buffer Warmup({completedCnn})");
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

            Console.WriteLine($"Wait... {delaysec}sec. to start {threadCount} thread"); 
            Thread.Sleep(TimeSpan.FromSeconds(delaysec));

            var dtref = DateTime.Now.AddSeconds(120);
            for (int i = 0; i < threadCount; i++)
            {
                Thread thread = new(() =>
                {
                    Console.WriteLine($"Running");
                    while (true)
                    {
                        if (DateTime.Now > dtref)
                        {
                            Console.WriteLine($"wait 90 seconds idle");
                            Thread.Sleep(TimeSpan.FromSeconds(90));
                            dtref = DateTime.Now.AddSeconds(120);
                            Console.WriteLine($"Running");
                        }
                        using var bufferedItem = modelRingBuffer!.Accquire();
                        if (bufferedItem.Successful)
                        {

                            var body = new ReadOnlyMemory<byte>(messageBodyBytes);

                            IBasicProperties props = bufferedItem.Current.CreateBasicProperties();
                            props.ContentType = "text/plain";
                            props.DeliveryMode = 1;
                            try
                            {
                                bufferedItem.Current.BasicPublish("", "log", false, props, body);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"{modelRingBuffer.Name} buffer is invalid! : {ex.Message}");
                                bufferedItem.Invalidate();
                            }
                        }
                        else
                        {
                            //do something! no buffer available
                        }

                    }
                });
                thread.Start();
                threads.Add(thread);
            }

            foreach (Thread thread in threads)
                thread.Join();

        }
    }
}
