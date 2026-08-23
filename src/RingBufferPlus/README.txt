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
The RingBufferPlus implementation follows the basic principle. The principle was expanded to have a scale capacity to optimize the consumption of the resources used.

Features
========
- Conscious use of resources
    - Designed to reduce buffer resources when unused
        - **Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal.
        - **Under low usage conditions**, the RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.
- Set a unique name per buffer instance
- Explicit FixedCapacity or ElasticCapacity (initial/min/max) modes
- ScaleUp / ScaleDown, automatic (on acquire fault) or manual (elastic buffer)
- HeartBeat: at each pulse, an item is acquired from the buffer for evaluation asynchronously
- Native observability: OpenTelemetry-compatible metrics (Meter) and traces (ActivitySource), no extra dependency
- Set a user function for errors (optional)
- Command to invalidate and renew an acquired item
- Command to warm up to full capacity before starting the application (optional but **recommended**)
- Receive an item from the buffer with **success/failure** information and **elapsed time** for acquisition
- Sets a **time limit** for acquiring the item in the buffer
- Simple and clear fluent syntax, fully async (IAsyncDisposable)

What's new in the latest version
=================================
- v5.0.0 (latest version) - complete, coordinated product overhaul with sweeping breaking changes.
  See CHANGELOG.md's "Breaking changes v5.0.0" section for the full list; highlights:
    - Concurrency core rewritten on System.Threading.Channels as a single state machine - no lock/semaphore.
    - IDisposable removed; IAsyncDisposable is now the sole disposal contract (await using / DisposeAsync()).
    - Public fluent builder surface redesigned around explicit, mutually exclusive FixedCapacity/ElasticCapacity modes,
      replacing Capacity/ScaleTimer/MinCapacity/MaxCapacity.
    - AutoScaleAcquireFault(...) and manual SwitchToAsync are mutually exclusive at the type level.
    - AcquireTimeout's delayAttempts parameter removed - a Channel-based acquire has no polling loop to pace.
    - Acquire no longer blocks while a scale operation is in progress, regardless of LockWhenScaling.
    - Added native observability (Meter/ActivitySource) - see doc/guides/usage-observability.md.
    - v4.x and earlier receive no further fixes now that v5.0.0 has shipped.

Basic Usage
===========
This example uses RingBufferPlus with a fixed (non-elastic) capacity.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .FixedCapacity(3)
           .BuildWarmupAsync(cancellation);

Console.WriteLine($"Ring Buffer name({rb.Name}) created.");
Console.WriteLine($"Ring Buffer Current capacity is : {rb.CurrentCapacity}");

await using (var buffer = await rb.AcquireAsync(cancellation))
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

await rb.DisposeAsync();

Manual Scale Usage
===================
This example uses RingBufferPlus with an elastic capacity and manual scale.
The manual scaling up and down process is done in the background without locking buffer acquisition or the SwitchToAsync command.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .ElasticCapacity(initialCapacity: 6, minCapacity: 3, maxCapacity: 9)
           .BuildWarmupAsync(cancellation);

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity))
{
    //manual scale was not scheduled
    //do something
}

// ... later, e.g. once load has passed ...
await rb.SwitchToAsync(ScaleSwitch.InitCapacity);

await rb.DisposeAsync();

Trigger Scale Usage
===================
This example uses RingBufferPlus with autoscaling. Autoscaling (scaling up) occurs when there is a capacity acquisition failure. Scaling down occurs automatically in the background once resource availability is reached.
The background auto scale up/down process does not lock buffer acquisition.
Manual scaling (SwitchToAsync) is unavailable at the type level when using AutoScaleAcquireFault.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .AcquireTimeout(TimeSpan.FromMilliseconds(500))
           .ElasticCapacity(initialCapacity: 3, minCapacity: 2, maxCapacity: 4, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(5))
           .AutoScaleAcquireFault(numberOfFaults: 2)
           .BuildWarmupAsync(cancellation);

// no SwitchToAsync here - rb is a plain IRingBufferService<int>

await rb.DisposeAsync();

Lock Acquire/Switch Usage
===================================
When a scaling up or down operation is executed, acquisition or scale switching is not blocked by default.
In scenarios with heavy stress on the buffer resource, it may be preferable to wait for the switch to actually complete before proceeding - use LockWhenScaling() for that.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .ElasticCapacity(6, 3, 9)
           .LockWhenScaling()
           .BuildWarmupAsync(cancellation);

// with LockWhenScaling(): returns only after the buffer has actually reached MaxCapacity
// (or the scale-up was undone on timeout, reflected in the false result)
var reached = await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);

HeartBeat Usage
===============

There may be scenarios where you want to inspect an item in the buffer for some action (such as checking its health status). When this option is used periodically, an item is made available in the buffer for this need.
Return false to discard it (a replacement is created in its place); return true to keep it. The framework owns acquiring and returning the item - there is no disposable object to manage yourself.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .HeartBeat(MyHeartBeat, pulse: TimeSpan.FromSeconds(10))
           .FixedCapacity(6)
           .BuildWarmupAsync(cancellation);

static bool MyHeartBeat(int item)
{
     //do anything ex: health check
     return true;
}

RabbitMQ Usage
==============

This example uses RingBufferPlus to pool RabbitMQ channels for publishing with improved performance, using automatic scaling when an acquisition failure occurs.
Scaling down is performed automatically in the background once resource availability is reached.
Manual scaling (SwitchToAsync) is unavailable at the type level when using AutoScaleAcquireFault.

var connectionFactory = new ConnectionFactory()
{
    Port = 8087,
    HostName = "localhost",
    UserName = "guest",
    Password = "guest",
    ClientProvidedName = "PublisherRoleProgram"
};

var connectionRabbit = await connectionFactory.CreateConnectionAsync(cancellation);

static async Task<IChannel> ChannelFactory(IConnection connectionRabbit, CancellationToken cancellation) =>
    await connectionRabbit.CreateChannelAsync(cancellationToken: cancellation);

var rb = await RingBuffer<IChannel>.New("RabbitChannels")
           .Logger(logger)
           .Factory((token) => ChannelFactory(connectionRabbit, token))
           .ElasticCapacity(initialCapacity: 10, minCapacity: 5, maxCapacity: 20, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(10))
           .AutoScaleAcquireFault()
           .BuildWarmupAsync(cancellation);

await using (var buffer = await rb.AcquireAsync(cancellation))
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

Examples
========
For more examples, please refer to the Samples directory: https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples

Documentation
=============
The documentation is available in the Docs directory: https://github.com/FRACerqueira/RingBufferPlus/blob/main/src/docs/docindex.md

License
=======
Copyright 2022 @ Fernando Cerqueira
RingBufferPlus is licensed under the MIT license. See https://github.com/FRACerqueira/RingBufferPlus/blob/main/LICENSE
