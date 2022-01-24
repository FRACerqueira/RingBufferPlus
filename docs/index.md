# <img align="left" width="100" height="100" src="./images/icon.png"># **Welcome to RingBufferPlus**
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![Publish](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/publish.yml/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/publish.yml)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![License](https://img.shields.io/github/license/FRACerqueira/RingBufferPlus)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)

A generic circular buffer (ring buffer) in C# with Auto-Scaler, Health-Check and Report-Metrics.

## Help
- [Install](#install)
- [Implementation Example](#implementation-example)
- [Apis](#apis)
- [Properties](properties.md)
- [Supported Platforms](#supported-platforms)

# Documentation

A ring buffer is a memory allocation scheme where memory is reused (reclaimed) when an index, incremented modulo the buffer size, writes over a previously used location.
A ring buffer makes a bounded queue when separate indices are used for inserting and removing data. The queue can be safely shared between threads (or processors) without further synchronization so long as one processor enqueues data and the other dequeues it. (Also, modifications to the read/write pointers must be atomic, and this is a non-blocking queue--an error is returned when trying to write to a full queue or read from an empty queue).
Note that a ring buffer with n elements is usually used to implement a queue with n-1 elements--there is always one empty element in the buffer. Otherwise, it becomes difficult to distinguish between a full and empty queue--the read and write pointers would be identical in both cases.

## Install
[**Top**](#help)

RingBufferPlus was developed in c# with the **netstandard2.1, .NET 5 AND .NET6** target frameworks.

```
Install-Package RingBufferPlus [-pre]
```

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note:  [-pre]/[--prerelease] usage for pre-release versions_**
```
Install-Package PromptPlus [-pre]
```

```
dotnet add package PromptPlus [--prerelease]
```

**_Note:  [-pre]/[--prerelease] usage for pre-release versions_**

## Implementation Example
[**Top**](#help)

A complete usage example can be seen in the [**RingBufferPlus - RabbitMQ**](https://github.com/FRACerqueira/RingBufferPlus/tree/main/RingBufferPlusRabbit) project. This project is an implementation of RingBufferPlus for high volume publishing to RabbitMQ queues.

## Apis
[**Top**](#help)

Title | Details
--- | ---
[AliasName](aliasname.md) |  Set alias to RingBuffer.
[MaxScaler](maxscaler.md) |  Sets the maximum capacity of items in the buffer.
[MinScaler](minscaler.md) |  Sets the minimum capacity of items in the buffer..
[PolicyTimeoutAccquire](policytimeoutaccquire.md) | Sets the timeout policy for acquiring items from the buffer.
[PolicyTimeoutAccquireAsync](policytimeoutaccquire.md) | Sets the timeout policy for acquiring items from the buffer.
[DefaultTimeoutAccquire](defaulttimeoutaccquire.md) | Sets the default timeout for acquiring items from the buffer. 
[DefaultIntervalHealthCheck](defaultintervalhealthcheck.md) | Sets the default interval for performing the Integrity Check on a buffer item. 
[DefaultIntervalAutoScaler](defaultintervalautoscaler.md) | Sets the default interval to perform auto-scaling of buffer items.
[DefaultIntervalReport](defaultintervalreport.md) | Set the default interval to perform the metric reporting.
[Factory](factory.md) | Set create-function to an item in the buffer.
[FactoryAsync](factory.md) | Set create-function to an item in the buffer.
[HealthCheck](healthcheck.md) | Set the integrity function to a buffer item.
[HealthCheckAsync](healthcheck.md) | Set the integrity function to a buffer item.
[AutoScaler](autoscaler.md) | Set the auto-scaling function of buffer items.
[AutoScalerAsync](autoscaler.md) | Set the auto-scaling function of buffer items.
[MetricsReport](metricsreport.md) | Set action for metrics report.
[MetricsReportAsync](metricsreport.md) | Set action for metrics report.
[AddLogProvider](addlogprovider.md) | Set log provider and default message level.
[Build](ringbufferbuild.md) | Executes and validates all commands, provides the events to be configured and the execution command.
[ErrorCallBack](errorcallback.md) | Error return event.
[TimeoutCallBack](timeoutcallback.md) | Timeout return event.
[AutoScaleCallback](autoscalecallback.md) | Auto-Scaler return event.
[Run](ringbufferrun.md) | Performs instance creation and provides command to acquire buffer item.
[Accquire](accquire.md) | Acquire an item from the buffer.
[AccquireAsync](accquire.md) | Acquire an item from the buffer.
[Metric class](metricclass.md) | Metric class details.
[Buffer class](bufferclass.md) | Ring buffer return class details by Accquire method.

## Supported platforms
[**Top**](#help)

- Windows
- Linux (Ubuntu, etc)

## **License**

This project is licensed under the [MIT License](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)


