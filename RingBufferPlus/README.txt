  _____  _               ____         __  __            _____  _               
 |  __ \(_)             |  _ \       / _|/ _|          |  __ \| |              
 | |__) |_ _ __   __ _  | |_) |_   _| |_| |_ ___ _ __  | |__) | |    _   _ ___ 
 |  _  /| | '_ \ / _` | |  _ <| | | |  _|  _/ _ \ '__| |  ___/| |   | | | / __|
 | | \ \| | | | | (_| | | |_) | |_| | | | ||  __/ |    | |    | |___| |_| \__ \
 |_|  \_\_|_| |_|\__, | |____/ \__,_|_| |_| \___|_|    |_|    |______\__,_|___/
                  __/ |                                                        
                 |___/                                                          

**Welcome to RingBufferPlus**
-----------------------------------------------------------

A generic circular buffer (ring buffer) in C# with Auto-Scaler, Health-Check and Metrics-Report.
RingBufferPlus was developed in c# with the **netstandard2.1, .NET 5 AND .NET6 ** target frameworks.

RingBufferPlus was developed in c# with target frameworks:

- netstandard2.1
- .NET 5
- .NET 6

**visit the official pages for complete documentation** :

https://fracerqueira.github.io/RingBufferPlus

**Relase Notes RingBufferPlus (V1.0.0)**
----------------------------------------
- Revised all internal events for async/Threads
- Added external CancellationToken to Acquire method
- Added Warmup AutoScaler to delay the first run.
- Refactored method names for better understanding and standardization
- Console project removed and demo web project created. Closer view of the real world
- Revised test project (in progress)
- Revised documentation (in progress)

**RingBufferPlus - Sample Minimum Usage**
-----------------------------------------

public class MyClass
{
   private readonly Guid _id;
   public MyClassTest()
   {
      _id = Guid.NewGuid();
   }
   public Guid Id => _id;
}

var rb = RingBuffer<MyClass>
        .CreateBuffer() //default 2 (Initial/min/max)
        .MaxBuffer(10)
        .Factory((ctk) => new MyClass())
        .Build()
        .Run();

using (var buffer = rb.Accquire())
{ 
   Console.WriteLine(buffer.Id);
}

rb.Dispose();

**RingBufferPlus - Sample Complex Usage**
-----------------------------------------

public class MyClass : IDisposable
{
   private readonly Guid _id;
   private bool _disposedValue;

   public MyClassTest()
   {
      _id = Guid.NewGuid();
   }
   public bool IsValidState => true;	
   public Guid Id => _id;
   protected virtual void Dispose(bool disposing)
   {
      if (!_disposedValue)
      {
         if (disposing)
         {
           _disposedValue = true;
         }
      }
   }
   public void Dispose()
   {
      // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
   }
}

var build_rb = RingBuffer<MyClass>
                .CreateBuffer(5)
                .MinBuffer(3)
                .MaxBuffer(10)
                .AliasName("Test")
                .SetPolicyTimeout(RingBufferPolicyTimeout.UserPolicy, (metric,ctk) => true)
                .SetTimeoutAccquire(10)
                .SetIntervalAutoScaler(500)
                .SetIntervalHealthCheck(1000)
                .SetIntervalFailureState(TimeSpan.FromSeconds(30))
                .SetIntervalReport(1000)
                .LinkedFailureState(() => true)
                .Factory((ctk) => New MyClass() )
                .HealthCheck((buffer, ctk) => buffer.IsValidState)
                .MetricsReport((metric,ctk) => Console.WriteLine(metric.ErrorCount))
                .AddLogProvider(_loggerFactory,RingBufferLogLevel.Information)
                .AutoScaler((RingBufferMetric, CancellationToken) =>
                {
                   return 5;	
                })
                .Build();

build_rb.AutoScalerCallback += Ring_AutoScalerCallback;
build_rb.ErrorCallBack += Ring_ErrorCallBack;
build_rb.TimeoutCallBack += Ring_TimeoutCallBack;

var rb = build_rb.Run(cancellationToken);

using (var buffer = rb.Accquire())
{ 
   Console.WriteLine(buffer.Id);
}

rb.Dispose();

private void Ring_ErrorCallBack(object sender, RingBufferErrorEventArgs e)
{
   Console.WriteLine($"{e.Alias} => Error: {e.Error?.Message ?? "Null"}.");
}

private void Ring_TimeoutCallBack(object sender, RingBufferTimeoutEventArgs e)
{
   Console.WriteLine($"{e.Alias} => TimeOut = {e.ElapsedTime}");
}

private void Ring_AutoScalerCallback(object sender, RingBufferAutoScaleEventArgs e)
{
   Console.WriteLine($"{e.Alias} => {e.OldCapacity} to {e.NewCapacity}.");
}
