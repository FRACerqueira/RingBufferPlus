using Microsoft.Extensions.Logging;
using RingBufferPlus;
namespace RingBufferPlusConsoleSample
{
    internal class Program
    {
        private static ILogger? logger;
        static void Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddConsole();
            });
            logger = loggerFactory.CreateLogger<Program>();

            Random rnd = new();

            using var cts = new CancellationTokenSource();

            var rb = RingBuffer<int>.New("MyBuffer", cts.Token)
                .Capacity(8)
                .Logger(logger!)
                .Factory((cts) => { return rnd.Next(1, 10); })
                .MasterScale()
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
                        // Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
                        .RollbackWhenFreeGreaterEq()
                        // Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
                        //.TriggerByAccqWhenFreeGreaterEq() 
                .BuildWarmup(out var completed);

            #region show properties

            Console.WriteLine($"Ring Buffer Warmup({completed})");
            Console.WriteLine($"Ring Buffer Capacity({rb.Capacity})");
            Console.WriteLine($"Ring Buffer MinCapacity({rb.MinCapacity})");
            Console.WriteLine($"Ring Buffer MaxCapacity({rb.MaxCapacity})");
            Console.WriteLine($"Ring Buffer ScaleCapacity({rb.ScaleCapacity})");
            Console.WriteLine($"Ring Buffer AccquireTimeout({rb.AccquireTimeout})");
            Console.WriteLine($"Ring Buffer FactoryTimeout({rb.FactoryTimeout})");
            Console.WriteLine($"Ring Buffer FactoryIdleRetry({rb.FactoryIdleRetry})");
            Console.WriteLine($"Ring Buffer SampleUnit({rb.SampleUnit})");
            Console.WriteLine($"Ring Buffer SamplesCount({rb.SamplesCount})");
            Console.WriteLine($"Ring Buffer ScaleToMin({rb.ScaleToMin})");
            Console.WriteLine($"Ring Buffer RollbackFromMin({rb.RollbackFromMin})");
            Console.WriteLine($"Ring Buffer TriggerFromMin({rb.TriggerFromMin})");
            Console.WriteLine($"Ring Buffer ScaleToMax({rb.ScaleToMax})");
            Console.WriteLine($"Ring Buffer RollbackFromMax({rb.RollbackFromMax})");
            Console.WriteLine($"Ring Buffer TriggerFromMax({rb.TriggerFromMax})");

            #endregion

            using var buffer1 = rb.Accquire();
            using var buffer2 = rb.Accquire();

            Console.WriteLine($"Buffer is ok({buffer1.Successful}:{buffer1.ElapsedTime}) : {buffer1.Current}");
            Console.WriteLine($"Buffer is ok({buffer1.Successful}:{buffer2.ElapsedTime}) : {buffer2.Current}");

            using (var buffer3 = rb.Accquire())
            {
                Console.WriteLine($"Buffer is ok({buffer3.Successful}:{buffer3.ElapsedTime}) : {buffer3.Current}");
                buffer3.Invalidate();
            }

            Console.WriteLine("Press anykey to stop/close ring buffer");
            Console.ReadKey();

            cts.Cancel();

            Console.WriteLine("Press anykey to end");
            Console.ReadKey();

        }
    }
}
