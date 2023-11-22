# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:RingBuffer<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# RingBuffer&lt;T&gt;

Namespace: RingBufferPlus

Represents the RingBufferPlus extensions.

```csharp
public static class RingBuffer<T>
```

#### Type Parameters

`T`<br>
Type of buffer.

Inheritance [Object](https://docs.microsoft.com/en-us/dotnet/api/system.object) → [RingBuffer&lt;T&gt;](./ringbufferplus.ringbuffer-1.md)

## Methods

### <a id="methods-new"/>**New(String, Nullable&lt;CancellationToken&gt;)**

Create a new instance to commands of RingBufferPlus.

```csharp
public static IRingBuffer<T> New(string buffername, Nullable<CancellationToken> cancellation)
```

#### Parameters

`buffername` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
The unique name to RingBuffer.

`cancellation` [Nullable&lt;CancellationToken&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The [CancellationToken](https://docs.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken).

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md)


- - -
[**Back to List Api**](./apis.md)