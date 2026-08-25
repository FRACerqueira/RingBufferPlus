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

RingBufferPlus
==============

Stop provisioning for worst case. Pool it, scale it, let it breathe.

Project Description
====================

RingBufferPlus is a bounded, thread-safe pool for any expensive-to-create resource - database connections, RabbitMQ channels, HTTP clients, whatever your Factory builds. You get one back with AcquireAsync, you return it by disposing it, and the pool takes care of keeping enough of them around without you having to guess a number up front.
Under the hood it follows the classic ring buffer principle: a bounded, index-based queue that can be shared safely between threads without extra synchronization. RingBufferPlus extends that principle with elastic capacity, so the pool can grow and shrink at runtime instead of staying fixed.

Features
========
- Conscious use of resources
    - Designed to reduce buffer resources when unused
        - **Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal.
        - **Under low usage conditions**, the RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.
- Set a unique name per buffer instance
- Explicit FixedCapacity or ElasticCapacity (min/max/target) modes
- ScaleUp / ScaleDown: for an elastic buffer, a floor guard, a backlog-reactive signal, and a predictive Monitor are always active - plus an optional temporary manual pin (SwitchToAsync)
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
- v6.0.0 (latest released version) - complete, coordinated product overhaul with sweeping breaking changes.
  See CHANGELOG.md's "Breaking changes" section for v6.0.0 for the full list; highlights:
    - Concurrency model redesigned around four cooperating roles (Orquestrador/Fabrica/Remocao/Monitor) -
      scale-up and scale-down execution now run off the engine's own single-consumer thread.
    - Autoscale algorithm replaced: a sliding-window percentile + linear-regression trend Monitor, which can
      scale up predictively from a rising demand trend alone, plus an always-active floor guard and
      backlog-reactive signal - there is no more separate "automatic vs. manual" mode to opt into.
    - SwitchToAsync redefined as a temporary pin with a required duration; ElasticCapacity's parameters
      reshaped to (minCapacity, maxCapacity, target).
    - AutoScaleAcquireFault and BackgroundLogger removed entirely; OnError simplified to Action<Exception>.
    - HeartBeat redesigned to Func<T, bool>: return false to discard the item (a replacement is created in
      its place), true to keep it - there is no disposable object handed to the callback to manage.
    - WarmupRingBufferAsync removed - AddRingBuffer<T> now registers an IHostedService that warms up
      automatically during host startup.
    - v5.x and earlier receive no further fixes now that v6.0.0 has shipped.

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

Pinning Capacity Manually
=========================
Every elastic buffer already has a floor guard, a backlog-reactive signal, and a predictive Monitor active on its own (see "Elastic Autoscale Usage" below) - there is no separate manual-only mode. SwitchToAsync pins the buffer to a capacity for a required duration, substituting for the Monitor's own output for that long; the floor guard and backlog-reactive signal are never suppressed by an active pin.
This is done in the background without locking buffer acquisition or the SwitchToAsync command by default.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .ElasticCapacity(minCapacity: 3, maxCapacity: 9, target: 6)
           .BuildWarmupAsync(cancellation);

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(10)))
{
    //pin was not scheduled
    //do something
}

// ... later, once the pin's own duration has done its job ...
await rb.SwitchToAsync(ScaleSwitch.InitCapacity, TimeSpan.FromMinutes(1));

await rb.DisposeAsync();

Elastic Autoscale Usage
=======================
This example uses RingBufferPlus with autoscaling: the floor guard, backlog-reactive signal (reacts the instant a caller starts waiting, before AcquireTimeout can elapse), and a predictive Monitor (percentile + trend, can scale up or down) are all active automatically - no opt-in call needed.
The background auto scale up/down process does not lock buffer acquisition.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .AcquireTimeout(TimeSpan.FromMilliseconds(500))
           .ElasticCapacity(minCapacity: 2, maxCapacity: 4, target: 3, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(5))
           .BuildWarmupAsync(cancellation);

// SwitchToAsync is also available here to pin a capacity temporarily (see "Pinning Capacity
// Manually" above) - it is not a separate, mutually exclusive mode.

await rb.DisposeAsync();

Lock Acquire/Switch Usage
===================================
When a scaling up or down operation is executed, acquisition or scale switching is not blocked by default.
In scenarios with heavy stress on the buffer resource, it may be preferable to wait for the switch to actually complete before proceeding - use LockWhenScaling() for that.

Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
           .Logger(logger)
           .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
           .ElasticCapacity(minCapacity: 3, maxCapacity: 9, target: 6)
           .LockWhenScaling()
           .BuildWarmupAsync(cancellation);

// with LockWhenScaling(): returns only after the scale-up finishes, one way or the other -
// true if it fully reached MaxCapacity, false if it only partially completed before its own
// timeout (whatever capacity was actually gained is kept either way, not undone)
var reached = await rb.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(10));

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

This example uses RingBufferPlus to pool RabbitMQ channels for publishing with improved performance, autoscaling automatically to real publish pressure.
Scaling down is performed automatically in the background once resource availability is reached.

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
           .ElasticCapacity(minCapacity: 5, maxCapacity: 20, target: 10, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(10))
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
The documentation is available in the Docs directory: https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/api/docindex.md

License
=======
Copyright 2022 @ Fernando Cerqueira
RingBufferPlus is licensed under the MIT license. See https://github.com/FRACerqueira/RingBufferPlus/blob/main/LICENSE
