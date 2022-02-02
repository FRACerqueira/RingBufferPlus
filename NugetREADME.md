# **Welcome to RingBufferPlus**

A generic circular buffer (ring buffer) in C# with Auto-Scaler, Health-Check and Metrics-Report
RingBufferPlus was developed in c# with the **netstandard2.1, .NET 5 AND .NET6** target frameworks.

## **Official pages** :

#### **[Visit the RingBufferPlus official page for complete documentation](https://fracerqueira.github.io/RingBufferPlus)**

## **RingBufferPlus - Sample Minimum Usage**

```csharp
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
        .CreateBuffer() //default 2
        .Factory((ctk) => new MyClass())
        .Build()
        .Run();

using (var buffer = rb.Accquire())
{ 
   Console.WriteLine(buffer.Id);
}

rb.Dispose();

```

## **RingBufferPlus - Sample Complex Usage**

```csharp
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
                .LinkedFailureState(() => true)
                .PolicyTimeoutAccquire(RingBufferPolicyTimeout.UserPolicy, (metric,ctk) => true)
                .DefaultTimeoutAccquire(10)
                .DefaultIntervalAutoScaler(500)
                .DefaultIntervalHealthCheck(1000)
                .DefaultIntervalOpenCircuit(TimeSpan.FromSeconds(30))
                .DefaultIntervalReport(1000)
                .Factory((ctk) => New MyClass() )
                .HealthCheck((buffer, ctk) => buffer.IsValidState)
                .MetricsReport((metric,ctk) => Console.WriteLine(metric.ErrorCount))
                .AddLogProvider(RingBufferLogLevel.Information, _loggerFactory)
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
   Console.WriteLine($"{e.Alias}/{e.Source} => TimeOut = {e.ElapsedTime}/{e.Timeout} Erros={e.Metric.ErrorCount} Overload = {e.Metric.OverloadCount}.");
}

private void Ring_AutoScalerCallback(object sender, RingBufferAutoScaleEventArgs e)
{
   Console.WriteLine($"{e.Alias} => {e.OldCapacity} to {e.NewCapacity}.Error/Timeout = {e.Metric.ErrorCount}/{e.Metric.TimeoutCount}");
}

```

## **License**

This project is licensed under the [MIT License](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)

