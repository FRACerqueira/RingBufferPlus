# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBufferScaleCapacity<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBufferScaleCapacity&lt;T&gt;

Namespace: RingBufferPlus

Represents the scale capacity commands to RingBufferPlus.

```csharp
public interface IRingBufferScaleCapacity<T>
```

#### Type Parameters

`T`<br>
Type of buffer.

## Methods

### <a id="methods-build"/>**Build()**

Validate and generate RingBufferPlus to service mode.

```csharp
IRingBufferService<T> Build()
```

#### Returns

[IRingBufferService&lt;T&gt;](./ringbufferplus.iringbufferservice-1.md).

### <a id="methods-buildwarmup"/>**BuildWarmup(ref Boolean, Nullable&lt;TimeSpan&gt;)**

Validate and generate RingBufferPlus and warmup with full capacity ready or reaching timeout (default 30 seconds).

```csharp
IRingBufferService<T> BuildWarmup(ref Boolean fullcapacity, Nullable<TimeSpan> timeout)
```

#### Parameters

`fullcapacity` [Boolean&](https://docs.microsoft.com/en-us/dotnet/api/system.boolean&)<br>
True if Warmup has full capacity, otherwise false.

`timeout` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The Timeout to Warmup has full capacity. Default value is 30 seconds.

#### Returns

[IRingBufferService&lt;T&gt;](./ringbufferplus.iringbufferservice-1.md).

### <a id="methods-maxcapacity"/>**MaxCapacity(Int32)**

Maximum capacity.

```csharp
IRingBufferScaleMax<T> MaxCapacity(int value)
```

#### Parameters

`value` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
The maximum buffer.Value mus be greater or equal [IRingBuffer&lt;T&gt;.Capacity(Int32)](./ringbufferplus.iringbuffer-1.md#capacityint32)

#### Returns

[IRingBufferScaleMax&lt;T&gt;](./ringbufferplus.iringbufferscalemax-1.md).

### <a id="methods-mincapacity"/>**MinCapacity(Int32)**

Minimum capacity.

```csharp
IRingBufferScaleMin<T> MinCapacity(int value)
```

#### Parameters

`value` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
The minimal buffer. Value mus be greater or equal 1

#### Returns

[IRingBufferScaleMin&lt;T&gt;](./ringbufferplus.iringbufferscalemin-1.md).

### <a id="methods-reportscale"/>**ReportScale(Action&lt;RingBufferMetric, ILogger, Nullable&lt;CancellationToken&gt;&gt;)**

Extension point when capacity was changed.
 <br>Executes asynchronously.

```csharp
IRingBufferScaleCapacity<T> ReportScale(Action<RingBufferMetric, ILogger, Nullable<CancellationToken>> report)
```

#### Parameters

`report` [Action&lt;RingBufferMetric, ILogger, Nullable&lt;CancellationToken&gt;&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.action-3)<br>
The handler to action.

#### Returns

[IRingBufferScaleCapacity&lt;T&gt;](./ringbufferplus.iringbufferscalecapacity-1.md).

### <a id="methods-slave"/>**Slave(IRingBufferPlus)**

Set slave ring buffer.

```csharp
IRingBufferScaleCapacity<T> Slave(IRingBufferPlus slaveRingBuffer)
```

#### Parameters

`slaveRingBuffer` [IRingBufferPlus](./ringbufferplus.iringbufferplus.md)<br>
The slave Ring buffer.

#### Returns

[IRingBufferScaleCapacity&lt;T&gt;](./ringbufferplus.iringbufferscalecapacity-1.md).


- - -
[**Back to List Api**](./apis.md)