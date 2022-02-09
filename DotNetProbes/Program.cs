using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RingBufferPlus;
using RingBufferPlus.ObjectValues;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetProbes
{
    public class Program
    {
        private static IConfiguration Appconfiguration;
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RingBufferPlus.json"), true);

            builder.Services.AddControllers();

            builder.Services
                .AddEndpointsApiExplorer()
                .AddSwaggerGen();

            builder.Services.AddHealthChecks()
                    .AddCheck("minimalLivenessCheck", () => HealthCheckResult.Healthy(),
                        new[] { HealthCheckTag.Live.ToString() })
                    .AddCheck("minimalReadinessCheck", () => HealthCheckResult.Healthy(),
                        new[] { HealthCheckTag.Ready.ToString() });

            builder.Services.AddSingleton<IConnectionFactory>((service) =>
            {
                return new ConnectionFactory
                {
                    ClientProvidedName = "RingBuffer",
                    HostName = Appconfiguration["RabbitMq:HostName"],
                    UserName = Appconfiguration["RabbitMq:UserName"],
                    Password = Appconfiguration["RabbitMq:Password"],
                    VirtualHost = Appconfiguration["RabbitMq:VirtualHost"]
                };
            });

            builder.Services.AddRingBuffer<IConnection>((service, loggerFactory, applifetime, ringbuffer) =>
            {
                var cnnfactory = service.GetService<IConnectionFactory>();
                return ringbuffer
                    .SetPolicyTimeout(RingBufferPolicyTimeout.Ignore)
                    .Factory((ctk) => cnnfactory.CreateConnection())
                    .HealthCheck((cnn, ctk) => cnn.IsOpen)
                    .AddLogProvider(loggerFactory)
                    .Build()
                    .Run(applifetime.ApplicationStopping);
            });

            builder.Services.AddRingBuffer<IModel>((service, loggerFactory, applifetime, ringbuffer) =>
            {
                var rbc = service.GetService<IRunningRingBuffer<IConnection>>();
                var cnnfactory = service.GetService<IConnectionFactory>();
                return ringbuffer
                    .InitialBuffer(20)
                    .MinBuffer(2)
                    .SetTimeoutAccquire(5000)
                    .LinkedFailureState(() => rbc.CurrentState.FailureState)
                    .Factory((ctk) => CreateModel(rbc))
                    .HealthCheck((model, ctk) => HCModel(model))
                    .SetIntervalAutoScaler(DefaultValues.IntervalScaler,TimeSpan.FromSeconds(30))
                    .AutoScaler(MyAutoScalerModel)
                    .SetIntervalReport(TimeSpan.FromSeconds(10))
                    .MetricsReport(MyMetricReportModel)
                    .AddLogProvider(loggerFactory)
                    .Build()
                    .Run(applifetime.ApplicationStopping);
            });

            var app = builder.Build();
            Appconfiguration = app.Configuration;

            app.UseSwagger();
            app.UseSwaggerUI();

            app.UseHealthCheckDefaults();

            app.UseRouting();
            app.UseAuthorization();
            app.MapControllers();

            //warmup IConnection
            app.Services.WarmupRingBuffer<IConnection>();
            //warmup IModel
            app.Services.WarmupRingBuffer<IModel>();

            await app.RunAsync();
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

        private static bool HCModel(IModel model)
        {
            return model.IsOpen;
        }

        private static int countDowntrendModel;
        private static int MyAutoScalerModel(RingBufferMetric arg, CancellationToken token)
        {
            //set new capacity
            int newcapacity = arg.State.CurrentCapacity;

            if (arg.State.MaximumCapacity == arg.State.MinimumCapacity)
            {
                newcapacity = arg.State.MinimumCapacity;
                countDowntrendModel = 0;
            }
            //has any try Accquire or has any retry or has any timeout to Accquire? then increment capacity
            //OverloadCount = counter of how many times it was not possible to acquire the item on the first attempt
            else if (!arg.State.FailureState && arg.AcquisitionCount > 0 && (arg.State.CurrentAvailable < 2 || arg.OverloadCount > 0 || arg.TimeoutCount > 0))
            {
                newcapacity++;
                if (newcapacity > arg.State.MaximumCapacity)
                {
                    newcapacity = arg.State.MaximumCapacity;
                }
                //reset countDowntrend
                countDowntrendModel = 0;
            }
            //has any try Accquire with not any retry or not any timeout ? then decrease capacity
            else if ((!arg.State.FailureState && arg.OverloadCount == 0 && arg.TimeoutCount == 0) || arg.AcquisitionCount == 0)
            {
                //countDowntrend = number of times there was the same downtrend
                countDowntrendModel++;
                if (countDowntrendModel >= 4)
                {
                    newcapacity--;
                    if (newcapacity < arg.State.MinimumCapacity)
                    {
                        newcapacity = arg.State.MinimumCapacity;
                    }
                    countDowntrendModel = 0;
                }
            }
            else
            {
                countDowntrendModel = 0;
            }
            return newcapacity;
        }

        private static void MyMetricReportModel(RingBufferMetric metric, CancellationToken toke)
        {
            Console.WriteLine($"\n[{DateTime.Now.ToLongTimeString()}] {metric.Alias} Report(10 sec) \n Avg.Exec(Ok): {metric.AverageSucceededExecution.TotalMilliseconds} ms. Accq(Ok/Err/Tout) : {metric.AcquisitionSucceededCount}/{metric.ErrorCount}/{metric.TimeoutCount}. Cap./Run./Aval. = {metric.State.CurrentCapacity}/{metric.State.CurrentRunning}/{metric.State.CurrentAvailable}\n");
        }
    }
}