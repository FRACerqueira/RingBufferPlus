# <img align="left" width="100" height="100" src="./images/icon.png"># **Welcome to RingBufferPlus**
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![Publish](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/publish.yml/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/publish.yml)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![License](https://img.shields.io/github/license/FRACerqueira/RingBufferPlus)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)

A generic circular buffer (ring buffer) in C# with Auto-Scaler, Health-Check and Report-Metrics.

## Help
- [Install](#install)
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


## Apis
[**Top**](#help)

Commands | Details
--- | ---
[AliasName](aliasname.md) |  Set alias to RingBuffer.
[MaxScaler](maxscaler.md) |  Sets the maximum capacity of items in the buffer.
[MinScaler](minscaler.md) |  Sets the minimum capacity of items in the buffer..
[PolicyTimeoutAccquire](policytimeoutaccquire.md) | Defines timeout policy for acquiring items from the buffer.

## Supported platforms
[**Top**](#help)

- Windows
- Linux (Ubuntu, etc)

## **License**

This project is licensed under the [MIT License](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)


