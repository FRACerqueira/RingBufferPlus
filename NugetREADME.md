# **Welcome to RingBufferPlus**

### **RingBufferPlus A generic circular buffer (ring buffer) in C# with Auto-Scaler.**

**RingBufferPlus** was developed in C# with the **netstandard2.1**, **.NET 6** , **.NET 7** and **.NET 8** target frameworks.

**[Visit the official page for more documentation of RingBufferPlus](https://fracerqueira.github.io/RingBufferPlus)**

## What's new in the latest version 
### V3.1.0 
[**Top**](#table-of-contents)

- Release with G.A
- Removed command 'FactoryHealth'
    - Check health not generic!.
- Upscaling does not need to remove the buffer
    - better performance and availability  
- Downscaling needs to remove all buffering
    - Performance penalty
    - Ensure consistency and relationship between Master and slave
- Created recovery state functionality
    - start/restart under fault conditions

## Features

### Implemented concept

The implementation follows the basic principle. The principle was expanded to have a scale capacity that may or may not be modified to optimize the consumption of the resources used.

### Key Features

**Under stressful conditions**, the RingBufferPlus tends to go to **maximum capacity** and stay until conditions return to normal.

**Under low usage conditions**, The RingBufferPlus tends to go to **minimum capacity** and stay until conditions return to normal.

- Conscious use of resources
- Set unique name for same buffer type
- Set the buffer capacity
- Set the minimum and maximum capacity (optional)
    - Set the conditions for scaling to maximum and minimum (required)
        - Automatic condition values ​​based on capacity (value not required)
    - Upscaling does not need to remove the buffer
        - better performance and availability  
    - Downscaling needs to remove all buffering
        - Performance penalty
        - Ensure consistency and relationship between Master and slave
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
- Detailed information about operations when the minimum log is Debug/Trace
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

