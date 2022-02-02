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
        private const string QueueName = "RingBufferTest";

        public MainProgram(IHostApplicationLifetime appLifetime, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _appLifetime = appLifetime;
            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping);
            _stoppingCts.Token.Register(() =>
            {
                _appLifetime.StopApplication();
            });
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Testtask = RunPOC(cancellationToken);
            return Testtask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Testtask == null)
            {
                return;
            }
            _stoppingCts.Cancel();
            try
            {
                await Task.WhenAny(Testtask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
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
                    result.ConfirmSelect();
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

        private Task RunPOC(CancellationToken cancellationToken)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US"); ;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");


            //default Connection for local rabbitmq
            var cnnfactory = new ConnectionFactory
            {
                ClientProvidedName = "RingBuffer",
                RequestedHeartbeat = TimeSpan.FromSeconds(12),
                AutomaticRecoveryEnabled = false,
            };

            using (var cnn = cnnfactory.CreateConnection())
            {
                using (var md = cnn.CreateModel())
                {

                    md.QueueDeclare(QueueName, true, false, false);
                }
            }

            var build_ringCnn = RingBuffer<IConnection>
                    .CreateBuffer()
                    .PolicyTimeoutAccquire(RingBufferPolicyTimeout.Ignore)
                    .Factory((ctk) => cnnfactory.CreateConnection())
                    .HealthCheck((cnn, ctk) => cnn.IsOpen)
                    .AddLogProvider(RingBufferLogLevel.Information, _loggerFactory)
                    .Build();

            //build_ringCnn.AutoScalerCallback += Ring_AutoScalerCallback;
            //build_ringCnn.ErrorCallBack += Ring_ErrorCallBack;
            //build_ringCnn.TimeoutCallBack += Ring_TimeoutCallBack;
            var ringCnn = build_ringCnn.Run(cancellationToken);


            var build_ringdmodel = RingBuffer<IModel>
                .CreateBuffer()
                .MaxBuffer(100)
                .LinkedFailureState(() => ringCnn.CurrentState.FailureState)
                .Factory((ctk) => CreateModel(ringCnn))
                .HealthCheck((model, ctk) => HCModel(model))
                .AutoScaler(MyAutoscalerModel)
                .DefaultIntervalReport(TimeSpan.FromMinutes(1))
                .MetricsReport((metric, _) => Console.WriteLine($"\n[{DateTime.Now.ToLongTimeString()}] {metric.Alias} Report(60 sec) \n Avg.Exec(Ok): {metric.AverageSucceededExecution.TotalMilliseconds} ms. Accq(Ok/Err/Tout) : {metric.AcquisitionSucceededCount}/{metric.ErrorCount}/{metric.TimeoutCount}. Cap./Run./Aval. = {metric.State.CurrentCapacity}/{metric.State.CurrentRunning}/{metric.State.CurrentAvailable}\n"))
                .AddLogProvider(RingBufferLogLevel.Information, _loggerFactory)
                .Build();

            //build_ringdmodel.AutoScalerCallback += Ring_AutoScalerCallback;
            //build_ringdmodel.ErrorCallBack += Ring_ErrorCallBack;
            //build_ringdmodel.TimeoutCallBack += Ring_TimeoutCallBack;

            var ringmodel = build_ringdmodel.Run(cancellationToken);


            var threadCount = 100;
            var messageBodyBytes = Encoding.UTF8.GetBytes("Hello World!");
            object Sync = new object();
            var hasdelay = false;
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

                                    bool? confirm = null;
                                    void handlerOk(object s, RabbitMQ.Client.Events.BasicAckEventArgs e)
                                    {
                                        //ASYNC CONFIRM
                                        confirm = true;
                                    }

                                    void handlerNOk(object s, RabbitMQ.Client.Events.BasicNackEventArgs e)
                                    {
                                        //ASYNC CONFIRM
                                        confirm = true;
                                    }

                                    ctx.Current.BasicAcks += handlerOk;
                                    ctx.Current.BasicNacks += handlerNOk;

                                    try
                                    {
                                        IBasicProperties props = ctx.Current.CreateBasicProperties();
                                        props.ContentType = "text/plain";
                                        props.DeliveryMode = 1;
                                        props.Expiration = "10000";
                                        ctx.Current.BasicPublish(exchange: "",
                                            routingKey: QueueName,
                                            mandatory: true,
                                            basicProperties: props,
                                            body: messageBodyBytes);

                                        //timeout must be greater than Heartbeat
                                        NaturalTimer.Delay(15000, () => confirm ?? false, null);
                                        if (confirm.HasValue && confirm.Value)
                                        {
                                            //MESSAGE SEND OK
                                        }
                                        else
                                        {
                                            //MESSAGE NOT SEND
                                        }
                                    }
                                    catch (RabbitMQ.Client.Exceptions.OperationInterruptedException rex)
                                    {
                                        ctx.Invalidate();
                                        Console.WriteLine($"{ctx.Alias} => Error: {rex}.");
                                    }
                                    finally
                                    {
                                        ctx.Current.BasicAcks -= handlerOk;
                                        ctx.Current.BasicNacks -= handlerNOk;
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

                    if (hasdelay)

                    {
                        Thread.Sleep(1000);
                    }

                }

                var deltk = threads.ToArray();

                try
                {
                    Task.WaitAll(deltk, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

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
                    hasdelay = !hasdelay;
                }
            }

            ringmodel.Dispose();
            ringCnn.Dispose();

            return Task.CompletedTask;
        }

        private int MyAutoscalerModel(RingBufferMetric arg, CancellationToken ctk)
        {
            //set new capacity
            int newcapacity = arg.State.CurrentCapacity;
            //has any try Accquire or has any TimeoutCount Accquire increment capacity
            if (!arg.State.FailureState && arg.OverloadCount > 0 || arg.TimeoutCount > 0)
            {
                //OverloadCount = wait avaliable
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
            else if (!arg.State.FailureState && LastAcquisitionCount <= arg.AcquisitionCount)
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