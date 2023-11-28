# <img align="left" width="100" height="100" src="./docs/images/icon.png">Welcome to RingBufferPlus
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

### **RingBufferPlus A generic circular buffer (ring buffer) in C# with Auto-Scaler.**

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

### V3.0.0 
[**Top**](#table-of-contents)

- Added command 'FactoryHealth'
    - Check health item before accquire buffer.
- Renamed Method 'SwithToScaleDefinitions' to 'MasterScale'
- Added master-slave feature(2 Ring Buffer with synchronization)
    - Added command set 'SlaveScale' to set report handler, Minimum and maximum capacity
- Added 'MasterSlave' enum item in SourceTrigger
- Added 'None' enum item in ScaleMode
- Revised to have greater performance without 'lock'
- Removed Method 'Counters'
    - data was not relevant and inaccurate
- Revised 'RingBufferMetric' 
    - Now only propreties 'Trigger', 'FromCapacity', 'ToCapacity' and 'MetricDate'

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

- Conscious use of resources
- Set unique name for same buffer type
- Set the buffer capacity
- Set buffer integrity (validate if the buffer is valid)
    - Verified with each acquiring
- Set the minimum and maximum capacity (optional)
    - Set the conditions for scaling to maximum and minimum (required)
        - Automatic condition values ​​based on capacity (value not required)
- Set master-slave (2 Ring Buffer with synchronization)
    - Master controls slave scale
- Event with scale change information
    - Executed in a separate thread asynchronously
- Associate the logger interface (optional)
- Define a user role for generated errors (optional)
    - Executed in a separate thread asynchronously
- Command to Invalidate the buffer when it is in an invalid state
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

### Sample-Console Usage (Minimal features with auto-scale)

```csharp
Random rnd = new();
var rb = RingBuffer<int>.New("MyBuffer", cts.Token)
    .Capacity(8)
    .Factory((cts) => { return rnd.Next(1, 10); })
    .MasterScale()
        .SampleUnit(TimeSpan.FromSeconds(10), 10)
        .MinCapacity(4)
            .ScaleWhenFreeGreaterEq()
            .RollbackWhenFreeLessEq()
        .MaxCapacity(20)
            .ScaleWhenFreeLessEq()
            .RollbackWhenFreeGreaterEq()
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


### Sample-api/web Usage (Minimal features without auto-scale)
[**Top**](#table-of-contents)

```csharp
builder.Services.AddRingBuffer<int>("Mybuffer",(ringbuf, _) =>
{
    return ringbuf
        .Capacity(8)
        .Factory((cts) => { return 10; })
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

### Sample-Console Master-Slave feature using RabbitMq (basic usage)
[**Top**](#table-of-contents) 

For more details see [**Complete-Samples**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBenchmarkSample).

```csharp
connectionRingBuffer = RingBuffer<IConnection>.New("RabbitCnn")
    .Capacity(2)
    .Logger(applogger!)
    .AccquireTimeout(TimeSpan.FromMilliseconds(500))
    .OnError((log, error) =>
        {
            log?.LogError("{error}", error);
        })
    .Factory((cts) => ConnectionFactory.CreateConnection())
    .FactoryHealth((item) => item.IsOpen)
    .SlaveScale()
        .MaxCapacity(10)
        .MinCapacity(1)
    .BuildWarmup(out completedCnn);

modelRingBuffer = RingBuffer<IModel>.New("RabbitChanels")
    .Capacity(10)
    .Logger(applogger!)
    .OnError((log, error) =>
        {
            log?.LogError("{error}", error);
        })
    .Factory((cts) => ModelFactory(cts))
    .FactoryHealth((item) => item.IsOpen)
    .MasterScale(connectionRingBuffer)
        .SampleUnit(TimeSpan.FromSeconds(10), 10)
        .MaxCapacity(50)
            .ScaleWhenFreeLessEq()
            .RollbackWhenFreeGreaterEq()
        .MinCapacity(2)
            .ScaleWhenFreeGreaterEq()
            .RollbackWhenFreeLessEq()
    .BuildWarmup(out completedChanels);
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
+------------------ +-------:+-----:+------------:+----------:+------------:+-------------:+------------:+------------:+------------:+------------+|
| Method            | Op/s   | Rank | Mean        | StdErr    | StdDev      | Min          | Q1          | Median      | Q3          | Max         |
|------------------ |-------:|-----:|------------:|----------:|------------:|-------------:|------------:|------------:|------------:|------------:|
| WithRingBuffer    | 6.8218 |    1 |    146.6 ms |   0.00 ms |     0.00 ms |    146.59 ms |    146.6 ms |    146.6 ms |    146.6 ms |    146.6 ms |
| WithRingBuffer    | 1.9411 |    2 |    515.2 ms | 101.72 ms | 1,017.24 ms |     72.76 ms |    300.1 ms |    439.2 ms |    508.2 ms | 10,426.5 ms |
| WithoutRingBuffer | 0.0676 |    3 | 14,797.6 ms | 133.34 ms | 1,333.36 ms | 13,306.95 ms | 14,061.5 ms | 14,497.3 ms | 15,098.1 ms | 21,286.4 ms |
| WithoutRingBuffer | 0.0662 |    4 | 15,109.9 ms |   0.00 ms |     0.00 ms | 15,109.93 ms | 15,109.9 ms | 15,109.9 ms | 15,109.9 ms |115,109.9 ms |
+------------------ +-------:+-----:+------------:+----------:+------------:+-------------:+------------:+------------:+------------:+------------+|
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

