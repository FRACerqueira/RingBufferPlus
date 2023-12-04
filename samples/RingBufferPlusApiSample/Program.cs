using RingBufferPlus;

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
                        .ScaleUnit(ScaleMode.Manual)
                            .MinCapacity(2)
                            .MaxCapacity(12)
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
