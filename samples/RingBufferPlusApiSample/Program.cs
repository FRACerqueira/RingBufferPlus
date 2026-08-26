// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Text.Json.Serialization;

namespace RingBufferPlusApiSample
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();


            builder.Services.AddRingBuffer<int>("Mybuffer",(ringbuf, services) =>
            {
                var applifetime = services.GetService<IHostApplicationLifetime>();
                return ringbuf
                        .Factory((cts) => { return Task.FromResult(10); })
                        .ElasticCapacity(2, 7, 5)
                        .Build(applifetime!.ApplicationStopping);
            });

            var app = builder.Build();

            // Warmup now happens automatically: AddRingBuffer<T> registers a hosted service that
            // runs it during the host's own startup.

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
