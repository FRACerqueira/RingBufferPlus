namespace RingBufferPlusApiSample
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();


            builder.Services.AddRingBuffer<int>("Mybuffer",(ringbuf, _) =>
            {
                return ringbuf
                        .Capacity(8)
                        .Factory((cts) => { return 10; })
                        .AccquireTimeout(TimeSpan.FromSeconds(10))
                        .OnError((log, error) => 
                        {
                            log?.LogError("{error}",error);
                        })
                        .SwithToScaleDefinitions()
                            .SampleUnit(TimeSpan.FromSeconds(60),60)
                            .ReportScale((mode, loger, metric, cts) =>
                            {
                                #pragma warning disable CA2254 // Template should be a static expression
                                loger.LogInformation($"Report [{metric.MetricDate}]  Trigger {metric.Trigger} : {mode} from {metric.FromCapacity} to {metric.ToCapacity} ({metric.Capacity}/{metric.MinCapacity}/{metric.MaxCapacity}) : {metric.FreeResource}");
                                #pragma warning restore CA2254 // Template should be a static expression
                            })
                            .MinCapacity(4)
                                // Defaut = Max  (Min = 1, Max = Capacity)
                                .ScaleWhenFreeGreaterEq()
                                // Defaut = Min  (Min = 1, Max = MinCapacity)
                                .RollbackWhenFreeLessEq()
                                // Defaut = Max-1 (Min = 1, Max = MinCapacity)
                                //.TriggerByAccqWhenFreeLessEq() 
                            .MaxCapacity(20)
                                // Default = Min (Min =  1, Max = Capacity)
                                .ScaleWhenFreeLessEq()
                                //Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
                                .RollbackWhenFreeGreaterEq()
                                // Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
                                //.TriggerByAccqWhenFreeGreaterEq()
                        .Build();
            });

            var app = builder.Build();

            app.WarmupRingBuffer<int>("Mybuffer");

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
