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

RingBufferPlus a generic circular buffer (ring buffer) in C# with auto-scaler.

Key Features
============

- Conscious use of resources
    - Designed to reduce buffer resources when unused
        - **Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal. 
        - **Under low usage conditions**, The RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.  
     - Designed to work on **container (or not) mitigating cpu and memory usage** (Avoiding k8s upscale/downscale unnecessarily)
- Start/restart under **Fault conditions and/or Stress conditions**
- Set unique name for same buffer type
- Set the **default capacity** (Startup)
- Set the **minimum and maximum capacity** (optional)
    - Set the conditions for scaling to maximum and minimum (optional)
        - Automatic condition values ​​based on capacity (value not required)
    - Upscaling does **not need to remove** the buffer
        - better performance and availability  
    - Downscaling does **not need to remove** the buffer when **no slave control**
        - better performance and availability  
    - Downscaling **needs to remove all** buffering when **has slave control**
        - Performance penalty
        - Ensure consistency and relationship between Master and slave
- Set scale type **Automatic** , **Manual** or **Slave**
    - Automatic: by free-resources on buffer
    - Manual: User/Application defined using 'Switch To' command
    - Slave : Indicates that the control is a slave scale type
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


Visit the official page for more documentation : 
https://fracerqueira.github.io/RingBufferPlus

PipeAndFilter was developed in C# with target frameworks:

- netstandard2.1
- .NET 6
- .NET 7
- .NET 8

*** What's new in V3.2.0 ***
============================

- Renamed command 'MasterScale' to 'ScaleUnit'
    - Added parameter 'ScaleUnit' to set the scale type (automatic/manual)
        - Now the user can manually set the scale change mode
- Removed command 'SlaveScale'
    - Now use 'ScaleUnit' command with scale type Slave
- Removed command 'SampleUnit'
    - Now time base unit and number of samples collected are parameters of the command 'ScaleUnit'
- Added new command 'SlaveControl' to set Slave Ringbuffer
    - Better clarity of command intent 
- Removed mandatory commands 'ScaleWhenFreeLessEq' , 'RollbackWhenFreeGreaterEq' for MaxCapacity commands
    - Now it is automatically set when 'MaxCapacity' is set  
- Removed mandatory commands 'ScaleWhenFreeGreaterEq' , 'RollbackWhenFreeLessEq' for MinCapacity commands
    - Now it is automatically set when 'MinCapacity' is set  
- Added new command 'SwithTo' for Ringbuffer service
    - Now the user can manually set the scale change when scale type is manual
- Improvement: Downscaling does not need to remove all buffer when no slave control
    - Better performance and availability 

Examples
========

See folder:
https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples

Generic Usage
=============

Sample-Console Usage (Minimal features with auto-scale)
-------------------------------------------------------

Random rnd = new();
var rb = RingBuffer<int>.New("MyBuffer", cts.Token)
    .Capacity(8)
    .Factory((cts) => { return rnd.Next(1, 10); })
    .ScaleUnit(ScaleMode.Automatic)
        .MinCapacity(4)
        .MaxCapacity(20)
    .BuildWarmup(out var completed);

...

using (var buffer = rb.Accquire(cts.Token))
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

Sample-api/web Usage (Minimal features without auto-scale)
----------------------------------------------------------

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

...

[ApiController]
[Route("[controller]")]
public class MyController(IRingBufferService<int> ringBufferService) : ControllerBase
{
    private readonly IRingBufferService<int> _ringBufferService = ringBufferService;

    [HttpGet]
    public ActionResult Get(CancellationToken token)
    {
        using (var buffer = _ringBufferService.Accquire(token))
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

Sample-api/web Usage (Minimal features with manual scale)
---------------------------------------------------------
builder.Services.AddRingBuffer<int>("Mybuffer",(ringbuf, _) =>
{
    return ringbuf
        .Capacity(8)
        .Factory((cts) => { return 10; })
        .ScaleUnit(ScaleMode.Manual)
            .MinCapacity(2)
            .MaxCapacity(12)
        .Build();
});

...

//If you do not use the 'Warmup Ring Buffer' command, the first access to acquire the buffer will be Warmup (not recommended)
app.WarmupRingBuffer<int>("Mybuffer");

...

[ApiController]
[Route("[controller]")]
public class MyController(IRingBufferService<int> ringBufferService) : ControllerBase
{
    private readonly IRingBufferService<int> _ringBufferService = ringBufferService;

    [HttpGet]
    public ActionResult Get(CancellationToken token)
    {
        using (var buffer = _ringBufferService.Accquire(token))
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

    [HttpPatch]
    [Route("/ChangeCapacity")]
    public ActionResult ChangeCapacity(ScaleSwith scaleUnit)
    {
        _ringBufferService.SwithTo(scaleUnit);
        return Ok();
    }
}

RabbitMQ Usage
==============

RabbitMQ has **AutomaticRecovery** functionality. This feature must be **DISABLED** when RingbufferPlus uses AutoScale.

If the AutomaticRecovery functionality is activated, "ghost" buffers may occur (without RingbufferPlus control)

For more details see https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBenchmarkSample

Sample-Console Master-Slave feature using RabbitMq (basic usage)
----------------------------------------------------------------

ConnectionFactory = new ConnectionFactory()
{
    ...
    AutomaticRecoveryEnabled = false
};

...

connectionRingBuffer = RingBuffer<IConnection>.New("RabbitCnn")
    .Capacity(2)
    .Logger(applogger!)
    .AccquireTimeout(TimeSpan.FromMilliseconds(500))
    .OnError((log, error) =>
        {
            log?.LogError("{error}", error);
        })
    .Factory((cts) => ConnectionFactory.CreateConnection())
    .ScaleUnit(ScaleMode.Slave)
        .ReportScale((metric, log, cts) =>
            {
                log?.LogInformation($"RabbitCnn Report: [{metric.MetricDate}]  Trigger {metric.Trigger} from {metric.FromCapacity} to {metric.ToCapacity}");
            })
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
    .ScaleUnit(ScaleMode.Automatic,10,TimeSpan.FromSeconds(10))
        .ReportScale((metric, log, cts) =>
            {
                log?.LogInformation($"RabbitChanels Report: [{metric.MetricDate}]  Trigger {metric.Trigger} from {metric.FromCapacity} to {metric.ToCapacity}");
            })
        .Slave(connectionRingBuffer)
        .MaxCapacity(50)
        .MinCapacity(2)
    .BuildWarmup(out completedChanels);


License
=======

Copyright 2022 @ Fernando Cerqueira
RingBufferPlus project is licensed under the  the MIT license.
