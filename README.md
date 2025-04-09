# ![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png) Welcome to RingBufferPlus

## **The generic ring buffer with auto-scaler (elastic buffer).** 

[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

## Table of Contents
- [Project Description](#project-description)
- [Features](#features)
- [Installing](#installing)
- [Usages](#usages)
- [RabbitMQ Usage](#rabbitmq-usage)
- [Examples](#examples)
- [Documentation](#documentation)
- [Code of Conduct](#code-of-conduct)
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)

## Project Description
[**Top**](#table-of-contents)

A ring buffer is a memory allocation scheme where memory is reused (reclaimed) when an index, incremented modulo the buffer size, writes over a previously used location. A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).

A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).

**The RingBufferPlus implementation follows the basic principle. The principle was expanded to have a scale capacity to optimize the consumption of the resources used.**

## Features
[**Top**](#table-of-contents)

- Conscious use of resources
    - Designed to reduce buffer resources when unused
        - **Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal. 
        - **Under low usage conditions**, The RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.  
- Set unique name for same buffer type
- Set the initial, minimum and maximum capacity
- ScaleUp / ScaleDown Automatic or manual (elastic buffer)
- Sets the HeartBeat : At each pulse, an item is acquired from the buffer for evaluation asynchronously.
- Set a user function for errors (optional)
- Set logger to execute in a separate thread asynchronously
- Command to Invalidate and renew the buffer
- Command to Warm up to full capacity before starting application (optional but **recommended**)
- Receive item from buffer with **success/failure** information and **elapsed time** for acquisition
- Sets a **time limit** for acquiring the item in the buffer
- Simple and clear fluent syntax

### What's new in the latest version 

- **v4.0.0 (latest version)**
    - Added support for .Net9, maintained support.Net8 
    - Removed support for .Net6, .Net7 and netstandard2.1
    - Some properties and commands have been refactored for readability or syntax errors. (Break changes)
    - Optimized several parts of the code to improve performance and consistency during auto/manual scaling.
    - Improved several commands to be asynchronous
    - Documentation updated
    - Bug fixed when used with Rabbitmq.
      - Removed need to set Automatic Recovery to false for use with Rabbitmq
    - Removed Master/Slave, ReportScale, BufferHealth,  ScaleWhen..., RollbackWhen... and TriggerByAccqWhen... concept (Break changes)
    - Added command LockWhenScaling 
    - Added command AutoScaleAcquireFault
    - Added command HeartBeat
    - Added command BackgroundLogger
    - Renamed command ScaleUnit ScaleTimer (Break changes)
    
- **v3.2.0 (Deprecated)**
    - Renamed command 'MasterScale' to 'ScaleUnit'
        - Added parameter 'ScaleUnit' to set the scale type (automatic/manual/Slave)
            - Now the user can manually set the scale change mode
    - Removed command 'SlaveScale'
        - Now use 'ScaleUnit' command with scale type Slave
    - Removed command 'SampleUnit'
        - Now time base unit and number of samples collected are parameters of the command 'ScaleUnit'
    - Added new command 'Slave' to set Slave Ringbuffer
        - Better clarity of command intent 
    - Removed mandatory commands 'ScaleWhenFreeLessEq' , 'RollbackWhenFreeGreaterEq' for MaxCapacity commands
        - Now it is automatically set when 'MaxCapacity' is set  
    - Removed mandatory commands 'ScaleWhenFreeGreaterEq' , 'RollbackWhenFreeLessEq' for MinCapacity commands
        - Now it is automatically set when 'MinCapacity' is set  
    - Added new command 'SwithTo' for Ringbuffer service
        - Now the user can manually set the scale change when scale type is manual
    - Improvement: Downscaling does not need to remove all buffer when no slave control
        - Better performance and availability 

## Installing
[**Top**](#table-of-contents)

```
Install-Package RingBufferPlus [-pre]
```

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note:  [-pre]/[--prerelease] usage for pre-release versions_**

## Usages
[**Top**](#table-of-contents)

### Basic Usage

This example uses the RingBufferPlus with non scale (non elastic buffer)

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(3)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity({rb.Capacity}) = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity({rb.MaxCapacity}) = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity({rb.MinCapacity}) = {rb.IsMinCapacity}.");

using (var buffer = await rb.AcquireAsync(token))
{
    if (buffer.Successful)
    {
        Console.WriteLine($"Buffer is ok({buffer.Successful}:{buffer.ElapsedTime}) value: {buffer.Current}");
    }
    else
    {
        //do something
    }
}
```

### Manual Scale Usage
[**Top**](#table-of-contents)

This example uses the RingBufferPlus with manual scale (elastic buffer). 

The manual scaling up and down process is done on brackgroud without locking buffer acquisition or SwitchTo command.

```csharp   
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .ScaleTimer()
                .MinCapacity(3)
                .MaxCapacity(9)
           .BuildWarmupAsync(token);

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity)) 
{
    //manual scale was not scheduled
    //do something
} 
```

### Trigger Scale Usage
[**Top**](#table-of-contents)

This example uses RingBufferPlus with autoscaling. Autoscaling (scaling up) occurs when there is a capacity acquisition failure. Scaling down automatically occurs in the background when a resource availability is reached.

The auto scale up and down process is done on brackgroud without locking buffer acquisition.

The Manual scaling up and down will be disabled (always returns false) when using the AutoScaleAcquireFault command

```csharp   
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .ScaleTimer(50, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault(2)
                .MinCapacity(3)
                .MaxCapacity(9)
           .BuildWarmupAsync(token);
```

### Lock Acquire/Switch Usage
[**Top**](#table-of-contents)

When the scaling up or down process is executed, acquisition or scale switching is not blocked.

In scenarios where there is a lot of stress on the buffer resource, it may not be possible to perform these actions. In these scenarios, it is preferable to block acquisition or scale switching to ensure the desired execution.

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .ScaleTimer(50, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .MinCapacity(3)
                .MaxCapacity(9)
           .BuildWarmupAsync(token);
```

### HeartBeat Usage
[**Top**](#table-of-contents)

There may be scenarios where you want to inspect an item in the buffer for some action (such as checking its health status). When this option is used periodically, an item is made available in the buffer for this need.

**You should not execute the dispose of the acquired item! This is done internally by the component.**

```csharp   
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .HeartBeat(MyHeartBeat)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);

private void MyHeartBeat(RingBufferValue<int> item)
{
     //do anything ex: health check
}
```

### Background Logger Usage
[**Top**](#table-of-contents)

Log execution is done automatically by the component (Level Debug, Warning and Error) in the same execution thread. This process can burden execution if the log recording process takes a long time. 

For this scenario, you can use the log execution in the background in an asynchronous process done by the component.

```csharp   
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .BackgroundLogger()
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);
```

## RabbitMQ Usage
[**Top**](#table-of-contents)

This example uses RingBufferPlus for Rabbit channels to publish messages with improved performance using automatic scaling when an acquisition failure occurs. 

Scaling down is performed automatically in the background when a resource availability is reached.

The auto scale up and down process is done on brackgroud without locking buffer acquisition.

The Manual scaling up and down will be disabled (always returns false) when using the AutoScaleAcquireFault command

```csharp   

var ConnectionFactory = new ConnectionFactory()
{
    Port = 8087,
    HostName = "localhost",
    UserName = "guest",
    Password = "guest",
    ClientProvidedName = "PublisherRoleProgram"
};

var connectionRabbit = await ConnectionFactory!.CreateConnectionAsync(token);

var rb = await RingBuffer<IChannel>.New("RabbitChanels")
           .Capacity(10)
           .Logger(applogger!)
           .BackgroundLogger()
           .Factory((cts) => ChannelFactory(cts))
           .ScaleTimer(100, TimeSpan.FromSeconds(10))
                .MaxCapacity(20)
                .MinCapacity(5)
                .AutoScaleAcquireFault()
            .BuildWarmupAsync(token);

using (var buffer = await rb.AcquireAsync(token))
{
    if (buffer.Successful)
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("Test"));
        await buffer.Current!.BasicPublishAsync("", "log", body);    
    }
    else
    {
        //do something
    }
}  

private async Task<IChannel> ChannelFactory(CancellationToken cancellation)
{
    return await connectionRabbit!.CreateChannelAsync(cancellationToken: cancellation);
}
```

## Examples
[**Top**](#table-of-contents)

For more examples, please refer to the [Samples directory](./samples) :

- [RingBufferPlusBasicSample](./samples/RingBufferPlusBasicSample)
    - This example uses the RingBufferPlus with non scale (non elastic buffer)
- [RingBufferPlusBasicManualScale](./samples/RingBufferPlusBasicManualScale)
    - This example uses the RingBufferPlus with manual scale (elastic buffer). 
- [RingBufferPlusApiSample](./samples/RingBufferPlusApiSample)
    - This example uses RingBufferPlus with manual scaling (elastic buffer) in an API.
- [RingBufferPlusBasicTriggerScale](./samples/RingBufferPlusBasicTriggerScale)
    - This example uses RingBufferPlus with autoscaling. Autoscaling (scaling up) occurs when there is a capacity acquisition failure.
- [RingBufferPlusRabbitSample](./samples/RingBufferPlusRabbitSample)
    - This example uses RingBufferPlus for Rabbit channels to publish messages with improved performance using automatic scaling when an acquisition failure occurs.



## Documentation
[**Top**](#table-of-contents)

The documentation is available in the [Docs directory](./src/docs/docindex.md).

## Code of Conduct
[**Top**](#table-of-contents)

This project has adopted the code of conduct defined by the Contributor Covenant to clarify expected behavior in our community.
For more information see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Contributing

See the [Contributing guide](CONTRIBUTING.md) for developer documentation.

## Credits
[**Top**](#table-of-contents)

This work was inspired by the project by [**Luiz Carlos Faria**](https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer). 

**API documentation generated by**

- [XmlDocMarkdown](https://github.com/ejball/XmlDocMarkdown), Copyright (c) 2024 [Ed Ball](https://github.com/ejball)
    - See an unrefined customization to contain header and other adjustments in project [XmlDocMarkdownGenerator](https://github.com/FRACerqueira/HtmlPdfPLus/tree/main/src/XmlDocMarkdownGenerator)  

## License
[**Top**](#table-of-contents)

Copyright 2022 @ Fernando Cerqueira

RingBufferPlus is licensed under the MIT license. See [LICENSE](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE).
