========================================================================================
 _____   _                ____           __   __             _____   _                 
 |  __ \ (_)              |  _ \         / _| / _|           |  __ \ | |                
 | |__) | _  _ __    __ _ | |_) | _   _ | |_ | |_  ___  _ __ | |__) || |     _   _  ___ 
 |  _  / | || '_ \  / _` ||  _ < | | | ||  _||  _|/ _ \| '__||  ___/ | |    | | | |/ __|
 | | \ \ | || | | || (_| || |_) || |_| || |  | | |  __/| |   | |     | |____| |_| |\__ \
 |_|  \_\|_||_| |_| \__, ||____/  \__,_||_|  |_|  \___||_|   |_|     |______|\__,_||___/
                     __/ |                                                              
                    |___/                                                               

========================================================================================

Welcome to RingBufferPlus
=========================

The generic ring buffer with auto-scaler (elastic buffer)

Project Description
====================

A ring buffer is a memory allocation scheme where memory is reused (reclaimed) when an index, incremented modulo the buffer size, writes over a previously used location. A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).
A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).
The RingBufferPlus implementation follows the basic principle. The principle was expanded to have a scale capacity to optimize the consumption of the resources used.

Features
========
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

What's new in the latest version 
================================

- v4.0.0 (latest version)
    - Added support for .Net9, maintained support.Net8 
    - Removed support for .Net6, .Net7 and netstandard2.1
    - Some properties and commands have been refactored for readability or syntax errors. (Break changes)
    - Optimized several parts of the code to improve performance and consistency during auto/manual scaling.
    - Improved several commands to be asynchronous
    - Documentation updated
    - Bug fixed when used with Rabbitmq.
      - Removed need to set Automatic Recovery to false for use with Rabbitmq
    - Removed Master/Slave, ReportScale, BufferHealth,  ScaleWhen..., RollbackWhen... and TriggerByAccqWhen... concept (Break changes)
    - Added command LockAcquireWhenAutoScale 
    - Added command AutoScaleAcquireFault
    - Added command HeartBeat
    - Added command BackgroundLogger
    - Renamed command ScaleUnit ScaleTimer (Break changes)
    

Basic Usage
===========
This example uses the RingBufferPlus with non scale (non elastic buffer)

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(3)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

Manual Scale Usage
===================
This example uses the RingBufferPlus with manual scale (elastic buffer). 
The manual scaling up and down process is done on brackgroud without locking buffer acquisition or SwitchTo command.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .ScaleTimer()
                .MinCapacity(3)
                .MaxCapacity(9)
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity)) 
{
    //manual scale was not scheduled
    //do something
}

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

Trigger Scale Usage
===================
This example uses RingBufferPlus with autoscaling. Autoscaling (scaling up) occurs when there is a capacity acquisition failure. Scaling down automatically occurs in the background when a resource availability is reached.
The auto scale up and down process is done on brackgroud without locking buffer acquisition.
The Manual scaling up and down will be disabled (always returns false) when using the AutoScaleAcquireFault command

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

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

Lock/Unlock de Acquire/Switch Usage
===================================
When the scaling up or down process is executed, acquisition or scale switching is not blocked.
In scenarios where there is a lot of stress on the buffer resource, it may not be possible to perform these actions. In these scenarios, it is preferable to block acquisition or scale switching to ensure the desired execution.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .ScaleTimer(50, TimeSpan.FromSeconds(5))
                .LockAcquireWhenAutoScale()
                .AutoScaleAcquireFault()
                .MinCapacity(3)
                .MaxCapacity(9)
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

HeartBeat Usage
===============

There may be scenarios where you want to inspect an item in the buffer for some action (such as checking its health status). When this option is used periodically, an item is made available in the buffer for this need.
**You should not execute the dispose of the acquired item! This is done internally by the component.**

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .HeartBeat(MyHeartBeat)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

private void MyHeartBeat(RingBufferValue<int> item)
{
     //do anything ex: health check
}

Background Logger Usage
=======================

Log execution is done automatically by the component (Level debug and Error) in the same execution thread. This process can burden execution if the log recording process takes a long time. 
For this scenario, you can use the log execution in the background in an asynchronous process done by the component.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Capacity(6)
           .Logger(HostApp.Services.GetService<ILogger<Program>>())
           .BackgroundLogger()
           .Factory((_) => { return Task.FromResult(rnd.Next(1, 10)); })
           .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

RabbitMQ Usage
==============

This example uses RingBufferPlus for Rabbit channels to publish messages with improved performance using automatic scaling when an acquisition failure occurs. 
Scaling down is performed automatically in the background when a resource availability is reached.
The auto scale up and down process is done on brackgroud without locking buffer acquisition.
The Manual scaling up and down will be disabled (always returns false) when using the AutoScaleAcquireFault command

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
           .Factory((cts) => ModelFactory(cts)!)
           .ScaleTimer(100, TimeSpan.FromSeconds(10))
                .MaxCapacity(20)
                .MinCapacity(5)
                .AutoScaleAcquireFault()
            .BuildWarmupAsync(token);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsInitCapacity = {rb.IsInitCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMaxCapacity = {rb.IsMaxCapacity}.");
Console.WriteLine($"Ring Buffer name({rb.Name}) IsMinCapacity = {rb.IsMinCapacity}.");

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

private async Task<IChannel> ModelFactory(CancellationToken cancellation)
{
    return await connectionRabbit!.CreateChannelAsync(cancellationToken: cancellation);
}

Examples
========
For more examples, please refer to the [Samples directory](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples) :

Documentation
=============
The documentation is available in the [Docs directory](https://github.com/FRACerqueira/RingBufferPlus/blob/main/src/docs/docindex.md).

License
=======
Copyright 2022 @ Fernando Cerqueira
RingBufferPlus is licensed under the MIT license. See [LICENSE](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE).

