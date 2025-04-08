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
                        .Capacity(5)
                        .Factory((cts) => { return Task.FromResult(10); })
                        .ScaleTimer()
                            .MinCapacity(2)
                            .MaxCapacity(7)
                        .Build(applifetime!.ApplicationStopping);
            });

            var app = builder.Build();

            await app.WarmupRingBufferAsync<int>("Mybuffer");

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
