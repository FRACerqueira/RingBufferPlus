# **Welcome to RingBufferPlus**

### **RingBufferPlus a generic circular buffer (ring buffer) in C# with auto-scaler.**

**RingBufferPlus** was developed in C# with the **netstandard2.1**, **.NET 6** , **.NET 7** and **.NET 8** target frameworks.

**[Visit the official page for more documentation of RingBufferPlus](https://fracerqueira.github.io/RingBufferPlus)**

## What's new in the latest version 
### V3.2.0 
[**Top**](#table-of-contents)

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

## Features

### Implemented concept

The implementation follows the basic principle. The principle was expanded to have a scale capacity that may or may not be modified to optimize the consumption of the resources used.

### Key Features

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

