﻿using BenchmarkDotNet.Analysers;
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
using Microsoft.Extensions.Logging;

namespace RingBufferPlusBenchmarkSample
{
    [Config(typeof(Config))]
    [SimpleJob(RunStrategy.ColdStart, RuntimeMoniker.Net80)]
    [RankColumn]
    public class BenchmarkProgram
    {
        private static ConnectionFactory? ConnectionFactory;
        private static IRingBufferService<IModel>? modelRingBuffer;
        private static IRingBufferService<IConnection>? connectionRingBuffer;
        ReadOnlyMemory<byte> message;

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


        private class Config : ManualConfig
        {
            public Config()
            {
                AddJob(Job.Dry);
                AddLogger(ConsoleLogger.Default);
                AddColumn(TargetMethodColumn.Method);
                AddColumn(StatisticColumn.AllStatistics);
                AddExporter(RPlotExporter.Default, CsvExporter.Default);
                AddAnalyser(EnvironmentAnalyser.Default);
                UnionRule = ConfigUnionRule.AlwaysUseLocal;
            }
        }

        public BenchmarkProgram()
        {
        }

        [GlobalSetup]
        public void GlobalSetup()
        {
            message = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("0"));

            ConnectionFactory = new ConnectionFactory()
            {
                Port = 8087,
                HostName = "localhost",
                UserName = "guest",
                Password = "guest",
                VirtualHost = "EnterpriseLog",
                AutomaticRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromMinutes(1),
                ClientProvidedName = "PublisherRoleProgram"
            };

            connectionRingBuffer = RingBuffer<IConnection>.New("RabbitCnn")
                .Capacity(5)
                .Logger(Program.logger!)
                .OnError((log, error) =>
                {
                    log?.LogError("{error}", error);
                })
                .Factory((cts) => ConnectionFactory.CreateConnection())
                .AccquireTimeout(TimeSpan.FromMilliseconds(1500))
                .BuildWarmup(out _);

            modelRingBuffer = RingBuffer<IModel>.New("RabbitChanels")
                .Capacity(20)
                .Logger(Program.logger!)
                .OnError((log, error) =>
                {
                    log?.LogError("{error}", error);
                })
                .Factory((cts) => ModelFactory(cts)!)
                .AccquireTimeout(TimeSpan.FromMilliseconds(1500))
                .BuildWarmup(out _);
        }

        [GlobalCleanup]
        public static void GlobalCleanup()
        {
            modelRingBuffer!.Dispose();
            connectionRingBuffer!.Dispose();
        }

        public static void CreateQueue(IModel model, string queueName) =>
            model.QueueDeclare(queueName, true, false, false, null);

        private static void Send(RingBufferValue<IModel>? rb, IModel channel,string queuename, ReadOnlyMemory<byte> message)
        {
            var props = channel.CreateBasicProperties();
            props.DeliveryMode = 1;
            try
            {
                channel.BasicPublish("", queuename, false, props, message);
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
        public int WithRingBuffer()
        {
            string queueName = $"WithRingBuffer-{Guid.NewGuid():D}";
            using (var accquisiton = modelRingBuffer!.Accquire())
            {
                CreateQueue(accquisiton.Current, queueName);
            }

            for (var i = 0; i < 5; i++)
                for (var j = 0; j < 1000; j++)
                    using (var accquisiton = modelRingBuffer.Accquire())
                    {
                        Send(accquisiton, accquisiton.Current, queueName, message);
                    }
            return 0;
        }

        [Benchmark(Baseline = true)]
        public int WithoutRingBuffer()
        {
            string queueName = $"WithoutRingBuffer-{Guid.NewGuid():D}";
            using (var connection = ConnectionFactory!.CreateConnection())
            {
                using var model = connection.CreateModel();
                CreateQueue(model, queueName);
            }

            for (var i = 0; i < 5; i++)
                using (var connection = ConnectionFactory!.CreateConnection())
                {
                    for (var j = 0; j < 1000; j++)
                        using (var model = connection.CreateModel())
                        {
                            Send(null, model, queueName, message);
                        }
                }
            return 0;
        }
    }    
}