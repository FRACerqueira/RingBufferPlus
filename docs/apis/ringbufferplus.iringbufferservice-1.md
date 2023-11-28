# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBufferService<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBufferService&lt;T&gt;

Namespace: RingBufferPlus

Represents the commands to RingBufferPlus service.

```csharp
public interface IRingBufferService<T> : IRingBufferSwith, System.IDisposable
```

#### Type Parameters

`T`<br>

Implements [IRingBufferSwith](./ringbufferplus.iringbufferswith.md), [IDisposable](https://docs.microsoft.com/en-us/dotnet/api/system.idisposable)

## Properties

### <a id="properties-accquiretimeout"/>**AccquireTimeout**

Timeout to accquire buffer. Default value is 30 seconds.

```csharp
public abstract TimeSpan AccquireTimeout { get; }
```

#### Property Value

[TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>

### <a id="properties-capacity"/>**Capacity**

Default capacity of ring buffer.

```csharp
public abstract int Capacity { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-factoryidleretry"/>**FactoryIdleRetry**

The delay time for retrying when a build fails. Default value is 30 seconds.

```csharp
public abstract TimeSpan FactoryIdleRetry { get; }
```

#### Property Value

[TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>

### <a id="properties-factorytimeout"/>**FactoryTimeout**

The timeout for build. Default value is 10 seconds.

```csharp
public abstract TimeSpan FactoryTimeout { get; }
```

#### Property Value

[TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>

### <a id="properties-maxcapacity"/>**MaxCapacity**

Maximum capacity.

```csharp
public abstract int MaxCapacity { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-mincapacity"/>**MinCapacity**

Minimum capacity.

```csharp
public abstract int MinCapacity { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-name"/>**Name**

Unique name to RingBuffer.

```csharp
public abstract string Name { get; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-rollbackfrommax"/>**RollbackFromMax**

Condition to scale down to default capacity.
 <br>The free resource collected must be greater than or equal to value.

```csharp
public abstract Nullable<Int32> RollbackFromMax { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

### <a id="properties-rollbackfrommin"/>**RollbackFromMin**

Condition to scale up to default capacity.
 <br>The free resource (averange collected) must be less than or equal to value.

```csharp
public abstract Nullable<Int32> RollbackFromMin { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

### <a id="properties-samplescount"/>**SamplesCount**

Number of samples collected.Default value is baseunit/10. Default value is 60.

```csharp
public abstract int SamplesCount { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-sampleunit"/>**SampleUnit**

The [TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan) interval to colleted samples.Default baseunit is 60 seconds.

```csharp
public abstract TimeSpan SampleUnit { get; }
```

#### Property Value

[TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>

### <a id="properties-scalecapacity"/>**ScaleCapacity**

If ring buffer hscapacity to scale

```csharp
public abstract bool ScaleCapacity { get; }
```

#### Property Value

[Boolean](https://docs.microsoft.com/en-us/dotnet/api/system.boolean)<br>

### <a id="properties-scaletomax"/>**ScaleToMax**

Condition to scale up to max capacity.
 <br>The free resource collected must be less than or equal to value.

```csharp
public abstract Nullable<Int32> ScaleToMax { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

### <a id="properties-scaletomin"/>**ScaleToMin**

Condition to scale down to min capacity.
 <br>The free resource collected must be greater than or equal to value.

```csharp
public abstract Nullable<Int32> ScaleToMin { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

### <a id="properties-triggerfrommax"/>**TriggerFromMax**

Condition to scale down to default capacity foreach accquire.
 <br>The free resource collected at aqccquire must be greater than or equal to value.

```csharp
public abstract Nullable<Int32> TriggerFromMax { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

### <a id="properties-triggerfrommin"/>**TriggerFromMin**

Condition to trigger to default capacity (check at foreach accquire).
 <br>The free resource collected at aqccquire must be less than or equal to value.

```csharp
public abstract Nullable<Int32> TriggerFromMin { get; }
```

#### Property Value

[Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>

## Methods

### <a id="methods-accquire"/>**Accquire(Nullable&lt;CancellationToken&gt;)**

Try Accquire value on buffer.
 <br>Will wait for a buffer item to become available or timeout (default 30 seconds).

```csharp
RingBufferValue<T> Accquire(Nullable<CancellationToken> cancellation)
```

#### Parameters

`cancellation` [Nullable&lt;CancellationToken&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The [CancellationToken](https://docs.microsoft.com/en-us/dotnet/api/system.threading.cancellationtoken).

#### Returns

[RingBufferValue&lt;T&gt;](./ringbufferplus.ringbuffervalue-1.md).


- - -
[**Back to List Api**](./apis.md)