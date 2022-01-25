using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace RingBufferPlusRabbit
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                    .UseConsoleLifetime()
                    .ConfigureServices((hostContext, services) =>
                    {
                        services.AddLogging(
                          builder =>
                          {
                              builder.AddConsole();
                              builder.SetMinimumLevel(LogLevel.Debug);
                              builder.AddFilter("Microsoft", LogLevel.Warning)
                                     .AddFilter("System", LogLevel.Warning);
                          });
                        services.AddHostedService<MainProgram>();
                    }).Build();
            await host.RunAsync();
        }
    }
}
