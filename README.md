# <img align="left" width="100" height="100" src="./docs/images/icon.png">Welcome to RingBufferPlus
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

### **RingBufferPlus A generic circular buffer (ring buffer) in C# with Auto-Scaler, and Report-Metrics.**

**RingBufferPlus** was developed in C# with the **netstandard2.1**, **.NET 6** , **.NET 7** and **.NET 8** target frameworks.

**[Visit the official page for more documentation of RingBufferPlus](https://fracerqueira.github.io/RingBufferPlus)**

## Table of Contents

- [What's new - previous versions](./docs/whatsnewprev.md)
- [Features](#features)
- [Installing](#installing)
- [Examples](#examples)
- [Usage](#usage)
- [Performance](#performance)
- [Code of Conduct](#code-of-conduct)
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)
- [API Reference](https://fracerqueira.github.io/RingBufferPlus/apis/apis.html)

## What's new in the latest version 
### V2.0.0 
[**Top**](#table-of-contents)

- Release G.A with .NET8 

## Features

### Basic concept
[**Top**](#table-of-contents)

A ring buffer is a memory allocation scheme where memory is reused (reclaimed) when an index, incremented modulo the buffer size, writes over a previously used location.

A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).

### Implemented concept
[**Top**](#table-of-contents)

The implementation follows the basic principle. The principle was expanded to have a scale capacity that may or may not be modified to optimize the consumption of the resources used.

![](./docs/images/RingBufferPlusFeature.png)

### Key Features
[**Top**](#table-of-contents)

- Set unique name for same buffer type
- Set the buffer capacity
- Set the minimum and maximum capacity (optional)
    - Set the conditions for scaling to maximum and minimum (required)
        - Automatic condition values ​​based on capacity (value not required)
    - Define a user role to receive capacity change events to log/save (optional)
        - Executed in a separate thread asynchronously
- Associate the logger interface (optional)
- Define a user role for generated errors (optional)
    - Executed in a separate thread asynchronously
- Invalidate the buffer when it is in an invalid state
- Warm up to full capacity before starting application 
- Receive item from buffer with success/failure information and elapsed time for acquisition
- Sets a time limit for acquiring the item in the buffer
- Detailed information about operations when the minimum log is Debug
- Simple and clear fluent syntax

## Installing
[**Top**](#table-of-contents)

```
Install-Package RingBufferPlus [-pre]
```

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note:  [-pre]/[--prerelease] usage for pre-release versions_**

## Examples
[**Top**](#table-of-contents)

See folder [**Samples**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples).

```
dotnet run --project [name of sample]
```

## Usage
[**Top**](#table-of-contents)

The **RingBufferPlus** use **fluent interface**; an object-oriented API whose design relies extensively on method chaining. Its goal is to increase code legibility. The term was coined in 2005 by Eric Evans and Martin Fowler.

### Sample-Console Usage (Full features)

```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Microsoft", LogLevel.Warning)
        .AddFilter("System", LogLevel.Warning)
        .AddConsole();
});
logger = loggerFactory.CreateLogger<Program>();
```

```csharp
var rb = RingBuffer<int>.New("MyBuffer", cts.Token)
    .Capacity(8)
    .Logger(logger!)
    .Factory((cts) => { return rnd.Next(1, 10); })
    .SwithToScaleDefinitions()
        .SampleUnit(TimeSpan.FromSeconds(10), 10)
        .ReportScale((mode, log, metric, _) =>
        {
            log.LogInformation($"{connectionRingBuffer!.Name} Report:  [{metric.MetricDate}]  Trigger {metric.Trigger} : {mode} from {metric.FromCapacity} to {metric.ToCapacity} ({metric.Capacity}/{metric.MinCapacity}/{metric.MaxCapacity}) : {metric.FreeResource}");
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
            // Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
            .RollbackWhenFreeGreaterEq()
            // Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)
            //.TriggerByAccqWhenFreeGreaterEq() 
    .BuildWarmup(out var completed);
```

```csharp
using (var buffer = rb.Accquire())
{
    if (bufferedItem.Successful)
    {
        try 
        {
            //do something    
        }
        catch
        {
            buffer.Invalidate();
        }
    }
}
```


### Sample-api/webUsage
[**Top**](#table-of-contents)

```csharp
builder.Services.AddRingBuffer<int>("Mybuffer",(ringbuf, _) =>
{
    return ringbuf
        .Capacity(8)
        .Factory((cts) => { return 10; })
        .AccquireTimeout(TimeSpan.FromMilliseconds(1500))
        .OnError((log, error) => 
        {
            log?.LogError("{error}",error);
        })
        .Build();
});

...
//If you do not use the 'Warmup Ring Buffer' command, the first access to acquire the buffer will be Warmup (not recommended)
app.WarmupRingBuffer<int>("Mybuffer");
```

```csharp
[ApiController]
[Route("[controller]")]
public class MyController(IRingBufferService<int> ringBufferService) : ControllerBase
{
    private readonly IRingBufferService<int> _ringBufferService = ringBufferService;

    [HttpGet]
    public ActionResult Get()
    {
        using (var buffer = _ringBufferService.Accquire())
        {
            if (bufferedItem.Successful)
            {
                try 
                {
                    //do something    
                }
                catch
                {
                    buffer.Invalidate();
                }
            }
        }
    }
}
```

## Performance
[**Top**](#table-of-contents)

The BenchmarkDotNet test was done on the local machine, with **'RabbitMQ' (over wsl)**. The measures are **about publisher** action (Scenario where Ringbuffer makes sense and brings significant performance gains).

**The gain can be much greater for real machines in production!**

See folder [**Samples/RingBufferPlusBenchmarkSample**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/Samples/RingBufferPlusBenchmarkSample).

```
BenchmarkDotNet v0.13.10, Windows 10 (10.0.19044.3693/21H2/November2021Update)
Intel Core i7-8565U CPU 1.80GHz (Whiskey Lake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.100
  [Host]     : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2
  Job-IMTEVT : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2
  Dry        : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2

| Method            | Mean        | StdErr    | StdDev      | Min         | Q1          | Median      | Q3          | Max         | Op/s   | Rank |
|------------------ |------------:|----------:|------------:|------------:|------------:|------------:|------------:|------------:|-------:|-----:|
| WithRingBuffer    |    589.8 ms |  16.90 ms |   169.01 ms |    191.2 ms |    487.2 ms |    555.4 ms |    659.0 ms |  1,324.8 ms | 1.6955 |    1 |
| WithoutRingBuffer | 15,441.5 ms | 154.39 ms | 1,543.90 ms | 13,785.4 ms | 14,562.8 ms | 15,071.1 ms | 15,981.9 ms | 24,595.2 ms | 0.0648 |    2 |
```

## Code of Conduct
[**Top**](#table-of-contents)

This project has adopted the code of conduct defined by the Contributor Covenant to clarify expected behavior in our community.
For more information see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Contributing

See the [Contributing guide](CONTRIBUTING.md) for developer documentation.

## Credits
[**Top**](#table-of-contents)

This work was inspired by the project by [**Luis Carlos Farias**](https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer). 
My thanks for your great work of bringing knowledge to the community!

**API documentation generated by**

- [xmldoc2md](https://github.com/FRACerqueira/xmldoc2md), Copyright (c) 2022 Charles de Vandière.

## License
[**Top**](#table-of-contents)

Copyright 2022 @ Fernando Cerqueira

RingBufferPlus is licensed under the MIT license. See [LICENSE](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE).

