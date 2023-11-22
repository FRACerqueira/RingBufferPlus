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
-------------------------

RingBufferPlus A generic circular buffer (ring buffer) in C# with Auto-Scaler, and Report-Metrics.

Features
--------
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

Visit the official page for more documentation : 
https://fracerqueira.github.io/RingBufferPlus

PipeAndFilter was developed in C# with target frameworks:

- netstandard2.1
- .NET 6
- .NET 7
- .NET 8

*** What's new in V2.0.0 ***
----------------------------

- Release G.A with .NET8 

**Examples**
------------
See folder:
https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples

**Usage**
---------

Sample-Console Usage (Full features)
====================================

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Microsoft", LogLevel.Warning)
        .AddFilter("System", LogLevel.Warning)
        .AddConsole();
});
logger = loggerFactory.CreateLogger<Program>();

... 

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

...

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

Sample-Sample-api/webUsage
==========================

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

app.WarmupRingBuffer<int>("Mybuffer");

...

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

**License**
-----------

Copyright 2022 @ Fernando Cerqueira
RingBufferPlus project is licensed under the  the MIT license.
