# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:HostingExtensions 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# HostingExtensions

Namespace: Microsoft.Extensions.DependencyInjection

Represents the commands to add RingBufferPlus in ServiceCollection and Warmup.

```csharp
public static class HostingExtensions
```

Inheritance [Object](https://docs.microsoft.com/en-us/dotnet/api/system.object) → [HostingExtensions](./microsoft.extensions.dependencyinjection.hostingextensions.md)

## Methods

### <a id="methods-addringbuffer"/>**AddRingBuffer&lt;T&gt;(IServiceCollection, String, Func&lt;IRingBuffer&lt;T&gt;, IServiceProvider, IRingBufferService&lt;T&gt;&gt;)**

Add RingBuffer in ServiceCollection.

```csharp
public static IServiceCollection AddRingBuffer<T>(IServiceCollection ServiceCollection, string buffername, Func<IRingBuffer<T>, IServiceProvider, IRingBufferService<T>> userfunc)
```

#### Type Parameters

`T`<br>
Type of buffer.

#### Parameters

`ServiceCollection` IServiceCollection<br>
The .

`buffername` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
The unique name to RingBuffer.

`userfunc` Func&lt;IRingBuffer&lt;T&gt;, IServiceProvider, IRingBufferService&lt;T&gt;&gt;<br>
The Handler to return the [IRingBufferService&lt;T&gt;](./ringbufferplus.iringbufferservice-1.md).

#### Returns

.

### <a id="methods-warmupringbuffer"/>**WarmupRingBuffer&lt;T&gt;(IHost, String, Nullable&lt;TimeSpan&gt;)**

Warmup RingBuffer with full capacity ready or reaching timeout .

```csharp
public static bool WarmupRingBuffer<T>(IHost appbluild, string buffername, Nullable<TimeSpan> timeout)
```

#### Type Parameters

`T`<br>
Type of buffer.

#### Parameters

`appbluild` IHost<br>
The .

`buffername` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
The unique name to RingBuffer.

`timeout` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The timeout for full capacity ready.

#### Returns

True if full capacity ready, otherwise false (Timeout but keeps running).


- - -
[**Back to List Api**](./apis.md)