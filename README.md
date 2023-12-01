# <img align="left" width="100" height="100" src="./docs/images/icon.png">Welcome to RingBufferPlus
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

### **RingBufferPlus A generic circular buffer (ring buffer) in C# with auto-scaler.**

**RingBufferPlus** was developed in C# with the **netstandard2.1**, **.NET 6** , **.NET 7** and **.NET 8** target frameworks.

**[Visit the official page for more documentation of RingBufferPlus](https://fracerqueira.github.io/RingBufferPlus)**

## Table of Contents

- [What's new - previous versions](./docs/whatsnewprev.md)
- [Features](#features)
- [Installing](#installing)
- [Examples](#examples)
- [**Generic Usage**](#generic-usage)
- [**RabbitMQ Usage**](#rabbitmq-usage)
- [**Performance**](#performance)
- [Code of Conduct](#code-of-conduct)
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)
- [API Reference](https://fracerqueira.github.io/RingBufferPlus/apis/apis.html)

## What's new in the latest version 

### V3.1.0 
[**Top**](#table-of-contents)

- Release with G.A
- Renamed command 'FactoryHealth' to 'BufferHealth'
    - Added parameter 'timeout' in 'BufferHealth'
        - Check internal health for all buffer when idle acquisition. Default value is 30 seconds.
- Upscaling does not need to remove the buffer
    - better performance and availability  
- Downscaling needs to remove all buffering
    - Performance penalty
    - Ensure consistency and relationship between Master and slave
- Created recovery state functionality
    - start/restart under fault conditions

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
    - Designed to reduce buffer resources when unused
        - **Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal. 
        - **Under low usage conditions**, The RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.  
     - Designed to work on **container (or not) mitigating cpu and memory usage** (Avoiding k8s upscale/downscale unnecessarily)
- Start/restart under **Fault conditions and/or Stress conditions**
- Set unique name for same buffer type
- Set the **default capacity** (Startup)
- Set the **minimum and maximum capacity** (optional)
    - Set the conditions for scaling to maximum and minimum (required)
        - Automatic condition values ​​based on capacity (value not required)
    - Upscaling does **not need to remove** the buffer
        - better performance and availability  
    - Downscaling **needs to remove** all buffering
        - Performance penalty
        - Ensure consistency and relationship between Master and slave
- Set **buffer integrity** for each acquisition and **check all integrity when acquisition idle**. (optional)
- Set master-slave (optional) - **2 Ring Buffer with synchronization**
    - Master controls slave scale
- Event with **scale change** information
    - Executed in a separate thread asynchronously
- Associate the **logger** interface (optional)
- Define a user function for generated **errors** (optional)
    - Executed in a separate thread asynchronously
- Command to **Invalidate and renew** the buffer when it is in an invalid state
- Warm up to full capacity **before starting application** (optional but **recommended**)
- Receive item from buffer with **success/failure** information and **elapsed time** for acquisition
- Sets a **time limit** for acquiring the item in the buffer
- Detailed information about operations when the minimum log is Debug/Trace (**not recommended**)
- Simple and clear **fluent syntax**

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

## Generic Usage
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

## RabbitMQ Usage
[**Top**](#table-of-contents)

The **RingBufferPlus** use **fluent interface**; an object-oriented API whose design relies extensively on method chaining. Its goal is to increase code legibility. The term was coined in 2005 by Eric Evans and Martin Fowler.

RabbitMQ has **AutomaticRecovery** functionality. This feature must be **DISABLED** when RinbufferPlus uses AutoScale.

_**If the AutomaticRecovery functionality is activated, "ghost" buffers may occur (without RinbufferPlus control)**_
### Sample-Console Master-Slave feature using RabbitMq (basic usage)

For more details see [**Complete-Samples**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBenchmarkSample).

```csharp
ConnectionFactory = new ConnectionFactory()
{
    ...
    AutomaticRecoveryEnabled = false
};
```

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
    .BufferHealth((buffer) => buffer.IsOpen)
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

The BenchmarkDotNet test **(5 x 1000 publish)** was done on the **local machine**, with **'RabbitMQ' (over wsl)**. The measures are **about publisher** action (Scenario where Ringbuffer makes sense and brings significant performance gains).

**The gain can be much greater for real machines in production!**

See folder [**Samples/RingBufferPlusBenchmarkSample**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/Samples/RingBufferPlusBenchmarkSample).

### _Notes for WithRingBufferScaler_

    - Default(02 connections and 10 channel) to Maximum(10 connections and 50 channel)

### _Notes for WithRingBuffer_

    - No Scale : Default = 10 connections and 50 channel

### Result

```
BenchmarkDotNet v0.13.10, Windows 10 (10.0.19044.3693/21H2/November2021Update)
Intel Core i7-8565U CPU 1.80GHz (Whiskey Lake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.100
  [Host]     : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2
  Job-IMTEVT : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2
  Dry        : .NET 8.0.0 (8.0.23.53103), X64 RyuJIT AVX2
+--------------------- +-------------+-----------+-------------+-------------+-------------+-------------+-------------+-------------+--------+------|
| Method               | Mean        | StdErr    | StdDev      | Min         | Q1          | Median      | Q3          | Max         | Op/s   | Rank |
|--------------------- |-------------|-----------|-------------|-------------|-------------|-------------|-------------|-------------|--------|------|
| WithRingBuffer       |    422.8 ms |  51.41 ms |    89.05 ms |    323.6 ms |    386.4 ms |    449.1 ms |    472.5 ms |    495.8 ms | 2.3649 |    1 |
| WithRingBufferScaler |    537.9 ms |  81.06 ms |   140.40 ms |    392.1 ms |    470.8 ms |    549.5 ms |    610.8 ms |    672.2 ms | 1.8591 |    2 |
| WithoutRingBuffer    | 84,961.4 ms | 752.59 ms | 1,303.52 ms | 84,198.0 ms | 84,208.8 ms | 84,219.6 ms | 85,343.1 ms | 86,466.5 ms | 0.0118 |    3 |
+--------------------- +-------------+-----------+-------------+-------------+-------------+-------------+-------------+-------------+--------+------|

Legends
-------
Mean   : Arithmetic mean of all measurements
StdErr : Standard error of all measurements
StdDev : Standard deviation of all measurements
Min    : Minimum
Q1     : Quartile 1 (25th percentile)
Median : Value separating the higher half of all measurements (50th percentile)
Q3     : Quartile 3 (75th percentile)
Max    : Maximum
Op/s   : Operation per second
Rank   : Relative position of current benchmark mean among all benchmarks (Arabic style)
1 ms   : 1 Millisecond (0.001 sec)
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
