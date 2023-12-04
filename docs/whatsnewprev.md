# <img align="left" width="100" height="100" src="./images/icon.png">RingBufferPlus What's new
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

### V3.1.0 
[**Main**](index.md) | [**Top**](#ringbufferplus-whats-new)

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
    - 
### V3.0.0 (Deprecate! CRITICAL BUGS)
[**Main**](index.md) | [**Top**](#ringbufferplus-whats-new)

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

### V2.0.0 (Deprecate! CRITICAL BUGS)
[**Main**](index.md) | [**Top**](#ringbufferplus-whats-new)

- Release with .NET8 

### V1.0.1 (Deprecate! CRITICAL BUGS)
[**Main**](index.md) | [**Top**](#ringbufferplus-whats-new)

- Internal Release
- Added optional graceful shutdown (Sync. discard instance) to the Warmup Ring Buffer extension when stopping the host application.
- Improvements to log and event messages with Resource file
- Fixed bug graceful shutdown
- Code cleanup

### V1.0.0 (Deprecate! CRITICAL BUGS)
[**Main**](index.md) | [**Top**](#ringbufferplus-whats-new)

- Internal Release

