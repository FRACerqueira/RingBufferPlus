# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBufferScaleMin<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBufferScaleMin&lt;T&gt;

Namespace: RingBufferPlus

Represents the MinCapacity commands to RingBufferPlus.

```csharp
public interface IRingBufferScaleMin<T>
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
The maximum buffer.

#### Returns

[IRingBufferScaleMax&lt;T&gt;](./ringbufferplus.iringbufferscalemax-1.md).

### <a id="methods-rollbackwhenfreelesseq"/>**RollbackWhenFreeLessEq(Nullable&lt;Int32&gt;)**

Condition to scale up to default capacity.
 <br>The free resource (averange collected) must be less than or equal to value.<br>RollbackWhenFreeLessEq do not used with TriggerByAccqWhenFreeLessEq command.<br>RollbackWhenFreeLessEq do not use with Manual/Slave scale

```csharp
IRingBufferScaleMin<T> RollbackWhenFreeLessEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number of averange collected.
 <br>Defaut = Min  (Min = 1, Max = MinCapacity).

#### Returns

[IRingBufferScaleMin&lt;T&gt;](./ringbufferplus.iringbufferscalemin-1.md).

### <a id="methods-scalewhenfreegreatereq"/>**ScaleWhenFreeGreaterEq(Nullable&lt;Int32&gt;)**

Condition to scale down to min capacity.
 <br>The free resource collected must be greater than or equal to value.<br>ScaleWhenFreeGreaterEq do not use with Manual/Slave scale

```csharp
IRingBufferScaleMin<T> ScaleWhenFreeGreaterEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number to free resource. 
 <br>Defaut = Max. (Min = 2, Max = Capacity).

#### Returns

[IRingBufferScaleMin&lt;T&gt;](./ringbufferplus.iringbufferscalemin-1.md).

### <a id="methods-triggerbyaccqwhenfreelesseq"/>**TriggerByAccqWhenFreeLessEq(Nullable&lt;Int32&gt;)**

Condition to trigger to default capacity (check at foreach accquire).
 <br>The free resource collected at aqccquire must be less than or equal to value.<br>TriggerByAccqWhenFreeLessEq do not used with RollbackWhenFreeLessEq command.<br>TriggerByAccqWhenFreeLessEq do not use with Manual/Slave scale

```csharp
IRingBufferScaleMin<T> TriggerByAccqWhenFreeLessEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number to trigger.
 <br>Defaut = Max-1 (Min = 2, Max = MinCapacity).

#### Returns

[IRingBufferScaleMin&lt;T&gt;](./ringbufferplus.iringbufferscalemin-1.md).


- - -
[**Back to List Api**](./apis.md)