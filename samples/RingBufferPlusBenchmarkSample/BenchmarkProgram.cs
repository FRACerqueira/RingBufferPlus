// ***************************************************************************************
// Original source code : Copyright 2020 Luis Carlos Farias.
// https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer
// Current source code : The maintenance and evolution is maintained by the RingBufferPlus project 
// ***************************************************************************************

using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using RabbitMQ.Client;
using RingBufferPlus;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace RingBufferPlusBenchmarkSample
{
    [Config(typeof(Config))]
    [RankColumn]
    public class BenchmarkProgram
    {
        private static ConnectionFactory? ConnectionFactory;
        private static IRingBufferService<IModel>? modelRingBuffer;
        private static IRingBufferService<IConnection>? connectionRingBuffer;
        private static ConnectionFactory? ConnectionFactory1;
        private static IRingBufferService<IModel>? modelRingBuffer1;
        private static IRingBufferService<IConnection>? connectionRingBuffer1;
        ReadOnlyMemory<byte> message;

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
                        if (connectionWrapper.Current.IsOpen)
                        {
                            model = connectionWrapper.Current.CreateModel();
                            if (model.IsOpen)
                            {
                                break;
                            }
                        }
                        else
                        {
                            connectionWrapper.Invalidate();
                        }
                    }
                }
                catch
                {
                }
                cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(5));
            }
            return model;
        }
        static IModel? ModelFactory1(CancellationToken cancellation)
        {
            IModel? model = null;
            while (!cancellation.IsCancellationRequested)
            {
                using var connectionWrapper = connectionRingBuffer1!.Accquire();
                try
                {
                    if (connectionWrapper.Successful)
                    {
                        if (connectionWrapper.Current.IsOpen)
                        {
                            model = connectionWrapper.Current.CreateModel();
                            if (model.IsOpen)
                            {
                                break;
                            }
                        }
                        else
                        {
                            connectionWrapper.Invalidate();
                        }
                    }
                }
                catch
                {
                }
                cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(5));
            }
            return model;
        }


        private class Config : ManualConfig
        {
            public Config()
            {
                AddJob(Job.LongRun
                        .WithLaunchCount(1)
                        .WithIterationCount(3)
                        .WithWarmupCount(3)
                        .WithToolchain(new InProcessNoEmitToolchain(timeout: TimeSpan.FromHours(1),true))
                        .WithStrategy(RunStrategy.Throughput));
                AddLogger(ConsoleLogger.Default);
                AddColumn(TargetMethodColumn.Method);
                AddColumn(StatisticColumn.AllStatistics);
                AddExporter(RPlotExporter.Default, CsvExporter.Default);
                AddAnalyser(EnvironmentAnalyser.Default);
                WithOptions(ConfigOptions.DisableOptimizationsValidator);
                UnionRule = ConfigUnionRule.AlwaysUseLocal;

            }
        }

        public BenchmarkProgram()
        {
        }

        [GlobalSetup(Target = "WithoutRingBuffer")]
        public void GlobalSetupWithoutRingBuffer()
        {
            message = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("0"));
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
        }

        [GlobalSetup(Target = "WithRingBuffer")]
        public void GlobalSetupRingBuffer()
        {
            message = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("0"));
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
                .Capacity(10)
                .Factory((cts) => ConnectionFactory.CreateConnection())
                .BufferHealth((buffer) => buffer.IsOpen)
                .AccquireTimeout(TimeSpan.FromMilliseconds(500))
                .BuildWarmup(out _);

            modelRingBuffer = RingBuffer<IModel>.New("RabbitChanels")
                .Capacity(50)
                .BufferHealth((buffer) => buffer.IsOpen)
                .Factory((cts) => ModelFactory(cts)!)
                .BuildWarmup(out _);
        }

        [GlobalSetup(Target = "WithRingBufferScaler")]
        public void GlobalSetupScaler()
        {
            message = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("0"));
            ConnectionFactory1 = new ConnectionFactory()
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

            connectionRingBuffer1 = RingBuffer<IConnection>.New("RabbitCnn")
                .Capacity(2)
                .Factory((cts) => ConnectionFactory1.CreateConnection())
                .AccquireTimeout(TimeSpan.FromMilliseconds(500))
                .SlaveScale()
                    .MaxCapacity(10)
                    .MinCapacity(1)
                .BuildWarmup(out _);

            modelRingBuffer1 = RingBuffer<IModel>.New("RabbitChanels")
                .Capacity(10)
                .Factory((cts) => ModelFactory1(cts)!)
                .BufferHealth((buffer) => buffer.IsOpen)
                .MasterScale(connectionRingBuffer1!)
                    .SampleUnit(TimeSpan.FromSeconds(10), 10)
                    .MaxCapacity(50)
                        .ScaleWhenFreeLessEq()
                        .RollbackWhenFreeGreaterEq()
                    .MinCapacity(2)
                        .ScaleWhenFreeGreaterEq()
                        .RollbackWhenFreeLessEq()
                .BuildWarmup(out _);
        }

        [GlobalCleanup()]
        public static void GlobalCleanup()
        {
            modelRingBuffer?.Dispose();
            connectionRingBuffer?.Dispose();
            modelRingBuffer1?.Dispose();
            connectionRingBuffer1?.Dispose();
        }

        private static void Send(RingBufferValue<IModel>? rb, IModel channel,ReadOnlyMemory<byte> message)
        {
            var props = channel.CreateBasicProperties();
            props.DeliveryMode = 1;
            try
            {
                channel.BasicPublish("", "log", false, props, message);
            }
            catch (Exception)
            {
                if (rb != null)
                {
                    rb!.Invalidate();
                }
            }
        }

        [Benchmark]
        public int WithoutRingBuffer()
        {
            for (var i = 0; i < 5; i++)
                using (var connection = ConnectionFactory!.CreateConnection())
                {
                    for (var j = 0; j < 1000; j++)
                        using (var model = connection.CreateModel())
                        {
                            Send(null, model, message);
                        }
                }
            return 0;
        }

        [Benchmark]
        public int WithRingBuffer()
        {
            for (var i = 0; i < 5; i++)
                for (var j = 0; j < 1000; j++)
                    using (var accquisiton = modelRingBuffer!.Accquire())
                    {
                        Send(accquisiton, accquisiton.Current,message);
                    }
            return 0;
        }


        [Benchmark]
        public int WithRingBufferScaler()
        {
            for (var i = 0; i < 5; i++)
                for (var j = 0; j < 1000; j++)
                    using (var accquisiton = modelRingBuffer1!.Accquire())
                    {
                        Send(accquisiton, accquisiton.Current, message);
                    }
            return 0;
        }
    }    
}
