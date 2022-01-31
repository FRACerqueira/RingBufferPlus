using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RingBufferPlus;
using RingBufferPlus.Events;
using RingBufferPlus.ObjectValues;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlusRabbit
{
    internal class MainProgram : IHostedService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private Task Testtask;
        readonly List<Task> threads = new List<Task>();
        private readonly CancellationTokenSource _stoppingCts;
        private long countReduceRage;
        private long LastAcquisitionCount;
        private readonly ILoggerFactory _loggerFactory = null;

        public MainProgram(IHostApplicationLifetime appLifetime, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _appLifetime = appLifetime;
            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping);
            _stoppingCts.Token.Register(() =>
            {
                if (!_appLifetime.ApplicationStopping.IsCancellationRequested)

                {
                    _appLifetime.StopApplication();
                    Environment.Exit(1);
                }
            });
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Testtask = Task.Run(() =>
            {
                try
                {
                    RunPOC(cancellationToken);
                }
                catch (Exception ex) 
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine(ex);
                    }
                }
                _stoppingCts.Cancel();
            }, cancellationToken);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Testtask == null)
            {
                return;
            }
            try
            {
                _stoppingCts.Cancel();
            }
            finally
            {
                await Task.WhenAny(Testtask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                _stoppingCts.Dispose();
            }
        }

        private static double RateMetric(RingBufferMetric arg) => ((double)arg.OverloadCount) / ((double)arg.AcquisitionCount == 0 ? 1 : arg.AcquisitionCount);

        private static async Task<IModel> CreateModelAsync(IRunningRingBuffer<IConnection> ringCnn)
        {
            //only demo not is best pratice
            await Task.Delay(0).ConfigureAwait(false);
            return CreateModel(ringCnn);

        }

        private static IModel CreateModel(IRunningRingBuffer<IConnection> ringCnn)
        {
            IModel result;
            using (var ctx = ringCnn.Accquire())
            {
                if (ctx.SucceededAccquire)
                {
                    result = ctx.Current.CreateModel();
                }
                else
                {
                    throw ctx.Error;
                }
            }
            return result;
        }

        private static async Task<bool> HCModelAsync(IModel model)
        {
            //only demo not is best pratice
            await Task.Delay(0).ConfigureAwait(false);
            return HCModel(model);
        }

        private static bool HCModel(IModel model)
        {
            if (model != null)
            {
                if (model.IsOpen)
                {
                    return true;
                }
                return false;
            }
            else
            {
                return false;
            }
        }

        private void RunPOC(CancellationToken cancellationToken)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US"); ;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");

            const string QueueName = "RingBufferTest";

            //default Connection for local rabbitmq
            var cnnfactory = new ConnectionFactory
            {
                ClientProvidedName = "RingBuffer",
                UseBackgroundThreadsForIO = true
            };

            using (var cnn = cnnfactory.CreateConnection())
            {
                using (var md = cnn.CreateModel())
                {

                    md.QueueDeclare(QueueName, true, false, false);
                }
            }

            var build_ringCnn = RingBuffer<IConnection>
                    .CreateBuffer(3)
                    .PolicyTimeoutAccquire(RingBufferPolicyTimeout.Ignore)
                    .DefaultIntervalOpenCircuit(TimeSpan.FromSeconds(30))
                    .Factory((ctk) => cnnfactory.CreateConnection())
                    .HealthCheck((cnn, ctk) =>
                    {
                        if (cnn != null)
                        {
                            if (cnn.IsOpen)
                            {
                                return true;
                            }
                            return false;
                        }
                        else
                        {
                            return false;
                        }
                    })
                    .AddLogProvider(RingBufferLogLevel.Information, _loggerFactory)
                    .Build();

            //build_ringCnn.AutoScalerCallback += Ring_AutoScalerCallback;
            //build_ringCnn.ErrorCallBack += Ring_ErrorCallBack;
            //build_ringCnn.TimeoutCallBack += Ring_TimeoutCallBack;
            var ringCnn = build_ringCnn.Run(cancellationToken);


            var build_ringdmodel = RingBuffer<IModel>
                .CreateBuffer(20)
                .MinBuffer(5)
                .MaxBuffer(102)
                .LinkedFailureState(() =>
                {
                    return ringCnn.CurrentState.FailureState;
                })
                .Factory((ctk) => CreateModel(ringCnn))
                .HealthCheck((model, ctk) => HCModel(model))
                .AutoScaler(MyAutoscalerModel)
                .DefaultIntervalReport(10000)
                .MetricsReport((metric, _) => Console.WriteLine($"\n[{DateTime.Now.ToLongTimeString()}] {metric.Alias} Report(10 sec) => Avg.Exec(Ok): {metric.AverageSucceededExecution.TotalMilliseconds} ms. Accq(Ok/Err) : {metric.AcquisitionSucceededCount}/{metric.ErrorCount}\n"))
                .AddLogProvider(RingBufferLogLevel.Information, _loggerFactory)
                .Build();

            //build_ringdmodel.AutoScalerCallback += Ring_AutoScalerCallback;
            //build_ringdmodel.ErrorCallBack += Ring_ErrorCallBack;
            //build_ringdmodel.TimeoutCallBack += Ring_TimeoutCallBack;

            var ringmodel = build_ringdmodel.Run(cancellationToken);


            var threadCount = 100;
            var messageBodyBytes = Encoding.UTF8.GetBytes("Hello World!");

            while (!cancellationToken.IsCancellationRequested)
            {

                for (int i = 0; i < threadCount; i++)
                {
                    var thread = Task.Run(() =>
                    {
                        var timer = new NaturalTimer();
                        timer.Start();
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            if (timer.TotalMinutes > 4)
                            {
                                threadCount--;
                                Console.WriteLine($"Stoping this Thread. Total running = {threadCount}");
                                //after 4 minutes end thread;
                                break;
                            }
                            using (var ctx = ringmodel.Accquire())
                            {
                                if (ctx.SucceededAccquire)
                                {
                                    IBasicProperties props = ctx.Current.CreateBasicProperties();
                                    props.ContentType = "text/plain";
                                    props.DeliveryMode = 1;
                                    props.Expiration = "10000";

                                    try
                                    {
                                        ctx.Current.BasicPublish(exchange: "",
                                            routingKey: QueueName,
                                            basicProperties: props,
                                            body: messageBodyBytes);
                                    }
                                    catch (Exception ex)
                                    {
                                        ctx.Invalidate();
                                        Console.WriteLine($"{ctx.Alias} => Error: {ex}.");
                                    }
                                }
                                else if (!ctx.State.FailureState)
                                {
                                    if (ctx.State.CurrentCapacity >= ctx.State.MaximumCapacity)
                                    {
                                        Console.WriteLine($"{ctx.Alias} => Error: {ctx.Error}.  Available/Running {ctx.State.CurrentAvailable}/{ctx.State.CurrentRunning}");
                                    }
                                }
                            }
                        }
                    }, cancellationToken);

                    threads.Add(thread);
                }

                var deltk = threads.ToArray();
                Task.WaitAll(deltk, CancellationToken.None);

                foreach (var item in deltk)
                {
                    item.Dispose();
                }
                threads.Clear();

                var timer = new NaturalTimer();
                timer.Start();

                while (timer.TotalSeconds < 90)
                {
                    Thread.Sleep(100);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    threadCount = 100;
                }
            }

            ringmodel.Dispose();
            ringCnn.Dispose();

        }

        private int MyAutoscalerModel(RingBufferMetric arg, CancellationToken ctk)
        {
            int newcapacity = arg.State.MaximumCapacity;
            if (arg.OverloadCount > 0 || arg.TimeoutCount > 0)
            {
                if (arg.OverloadCount >= 20)
                {
                    newcapacity += 20;
                }
                else if (arg.OverloadCount >= 10)
                {
                    newcapacity += 15;
                }
                else if (arg.OverloadCount > 5)
                {
                    newcapacity += 5;
                }
                else
                {
                    newcapacity++;
                }
                if (newcapacity > arg.State.MaximumCapacity)
                {
                    newcapacity = arg.State.MaximumCapacity;
                }
                //reset countReduceRage
                countReduceRage = 0;
            }
            else
            {
                if (arg.AcquisitionCount == 0)
                {
                    newcapacity -= 5;
                    if (newcapacity < arg.State.MinimumCapacity)
                    {
                        newcapacity = arg.State.MinimumCapacity;
                    }
                }
                else if (LastAcquisitionCount < arg.AcquisitionCount && arg.TimeoutCount == 0 && arg.State.CurrentAvailable > 1)
                {
                    countReduceRage++;
                    if (countReduceRage >= 2)
                    {
                        if (arg.State.CurrentAvailable > 2)
                        {
                            if (arg.State.CurrentAvailable > 7)
                            {
                                newcapacity -= 5;
                            }
                            else
                            {
                                newcapacity--;
                            }
                            if (newcapacity < arg.State.MinimumCapacity)
                            {
                                newcapacity = arg.State.MinimumCapacity;
                            }
                        }
                        countReduceRage = 0;
                    }
                }
            }
            LastAcquisitionCount = arg.AcquisitionCount;
            return newcapacity;
        }

        private void Ring_ErrorCallBack(object sender, RingBufferErrorEventArgs e)
        {
            Console.WriteLine($"{e.Alias} => Error: {e.Error?.Message ?? "Null"}.");
        }

        private void Ring_TimeoutCallBack(object sender, RingBufferTimeoutEventArgs e)
        {
            Console.WriteLine($"{e.Alias}/{e.Source} => TimeOut = {e.ElapsedTime}/{e.Timeout} Erros={e.Metric.ErrorCount} Overload = {e.Metric.OverloadCount}. Cap./Run./Aval. = {e.Metric.State.CurrentCapacity}/{e.Metric.State.CurrentRunning}/{e.Metric.State.CurrentAvailable}");
        }

        private void Ring_AutoScalerCallback(object sender, RingBufferAutoScaleEventArgs e)
        {
            Console.WriteLine($"{e.Alias} => {e.OldCapacity} to {e.NewCapacity}.Error/Timeout = {e.Metric.ErrorCount}/{e.Metric.TimeoutCount} Over = {e.Metric.OverloadCount} Acq./OverRate = {e.Metric.AcquisitionCount}/{RateMetric(e.Metric):P3} Cap./Run./Aval. = {e.Metric.State.CurrentCapacity}/{e.Metric.State.CurrentRunning}/{e.Metric.State.CurrentAvailable}");
        }
    }
}