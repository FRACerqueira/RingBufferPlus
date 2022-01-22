using RingBufferPlus;
using RingBufferPlus.Events;
using RingBufferPlus.Exceptions;
using RingBufferPlus.ObjectValues;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
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
        readonly List<Task> threads = new();
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
                    RunTest(cancellationToken);
                }
                catch (AggregateException ex) when (ex.InnerException is TaskCanceledException tex)
                {
                    if (!tex.CancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine(ex);
                    }
                }
                catch (TaskCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
                {
                    //ok
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
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
            using var ctx = ringCnn.Accquire();
            if (ctx.SucceededAccquire)
            {
                return ctx.Current.CreateModel();
            }
            throw ctx.Error;
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

        private void RunTest(CancellationToken cancellationToken)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US"); ;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");

            //default Connection for local rabbitmq
            var cnnfactory = new ConnectionFactory
            {
            };

            var ringCnn = RingBuffer<IConnection>
                .CreateRingBuffer(4)
                .MaxScaler(8)
                .PolicyTimeoutAccquire(RingBufferPolicyTimeout.Ignore)
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
                .Build()
                .Run(cancellationToken);

            var build_ringdmodel = RingBuffer<IModel>
                .CreateRingBuffer(10)
                .MinScaler(2)
                .MaxScaler(102)
                .DefaultTimeoutAccquire(800)
                .FactoryAsync((ctk) => CreateModelAsync(ringCnn))
                .HealthCheckAsync((model, ctk) => HCModelAsync(model))
                .AutoScaler(MyAutoscalerModel)
                .AddLogProvider(RingBufferLogLevel.Information,_loggerFactory)
                .Build();

            //build_ringdmodel.AutoScaleCallback += Ring_AutoScaleCallback;
            //build_ringdmodel.ErrorCallBack += Ring_ErrorCallBack;
            //build_ringCnn.TimeoutCallBack += Ring_TimeoutCallBack;

            var ringmodel = build_ringdmodel.Run(cancellationToken);

            var threadCount = 100;
            var messageBodyBytes = Encoding.UTF8.GetBytes("Hello World!");

            while (!cancellationToken.IsCancellationRequested)
            {

                for (int i = 0; i < threadCount; i++)
                {
                    var thread = Task.Run(async () =>
                    {
                        var timer = new NaturalTimer();
                        timer.Start();
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            if (timer.TotalSeconds > 120)
                            {
                                threadCount--;
                                Console.WriteLine($"Stoping this Thread. Total running = {threadCount}");
                                //after 3 minutes end thread;
                                break;
                            }
                            using (var ctx = await ringmodel.AccquireAsync().ConfigureAwait(false))
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
                                            routingKey: "Qtest",
                                            basicProperties: props,
                                            body: messageBodyBytes);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"{ctx.Alias} => Error: {ex.Message}.");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"{ctx.Alias} => Error: {ctx.Error?.Message ?? "Null"}.  Available {ctx.Available}");
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

                while (timer.TotalSeconds < 60)
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
            int newcapacity = arg.Capacity;
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
                if (newcapacity > arg.Maximum)
                {
                    newcapacity = arg.Maximum;
                }
                //reset countReduceRage
                countReduceRage = 0;
            }
            else
            {
                if (arg.AcquisitionCount == 0)
                {
                    newcapacity -= 5;
                    if (newcapacity < arg.Minimum)
                    {
                        newcapacity = arg.Minimum;
                    }
                }
                else if (LastAcquisitionCount < arg.AcquisitionCount && arg.TimeoutCount == 0)
                {
                    countReduceRage++;
                    if (countReduceRage >= 5)
                    {
                        if (arg.Avaliable > 2)
                        {
                            if (arg.Avaliable > 7)
                            {
                                newcapacity -= 5;
                            }
                            else
                            {
                                newcapacity--;
                            }
                            if (newcapacity < arg.Minimum)
                            {
                                newcapacity = arg.Minimum;
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
            Console.WriteLine($"{e.Alias}/{e.Source} => TimeOut = {e.ElapsedTime}/{e.Timeout} Erros={e.Metric.ErrorCount} Overload = {e.Metric.OverloadCount}. Cap./Run./Aval. = {e.Metric.Capacity}/{e.Metric.Running}/{e.Metric.Avaliable}");
        }

        private void Ring_AutoScaleCallback(object sender, RingBufferAutoScaleEventArgs e)
        {
            Console.WriteLine($"{e.Alias} => {e.OldCapacity} to {e.NewCapacity}.Error/Timeout = {e.Metric.ErrorCount}/{e.Metric.TimeoutCount} Over = {e.Metric.OverloadCount} Acq./OverRate = {e.Metric.AcquisitionCount}/{RateMetric(e.Metric):P3} Cap./Run./Aval. = {e.Metric.Capacity}/{e.Metric.Running}/{e.Metric.Avaliable}");
        }
    }
}