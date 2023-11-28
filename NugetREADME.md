# **Welcome to RingBufferPlus**

### **RingBufferPlus A generic circular buffer (ring buffer) in C# with Auto-Scaler, and Report-Metrics.**

**RingBufferPlus** was developed in C# with the **netstandard2.1**, **.NET 6** , **.NET 7** and **.NET 8** target frameworks.

**[Visit the official page for more documentation of RingBufferPlus](https://fracerqueira.github.io/RingBufferPlus)**

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

### Implemented concept

The implementation follows the basic principle. The principle was expanded to have a scale capacity that may or may not be modified to optimize the consumption of the resources used.

### Key Features

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

```
Install-Package RingBufferPlus [-pre]
```

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note:  [-pre]/[--prerelease] usage for pre-release versions_**

## Examples

See folder [**Samples**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples).

```
dotnet run --project [name of sample]
```

## License

Copyright 2022 @ Fernando Cerqueira

RingBufferPlus is licensed under the MIT license. See [LICENSE](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE).

