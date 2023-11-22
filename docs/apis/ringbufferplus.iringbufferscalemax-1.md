# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBufferScaleMax<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBufferScaleMax&lt;T&gt;

Namespace: RingBufferPlus

Represents the MaxCapacity commands to RingBufferPlus.

```csharp
public interface IRingBufferScaleMax<T>
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

### <a id="methods-mincapacity"/>**MinCapacity(Int32)**

Minimum capacity.

```csharp
IRingBufferScaleMin<T> MinCapacity(int value)
```

#### Parameters

`value` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
The minimum buffer.

#### Returns

[IRingBufferScaleMin&lt;T&gt;](./ringbufferplus.iringbufferscalemin-1.md).

### <a id="methods-rollbackwhenfreegreatereq"/>**RollbackWhenFreeGreaterEq(Nullable&lt;Int32&gt;)**

Condition to scale down to default capacity.
 <br>The free resource collected must be greater than or equal to value.<br>RollbackWhenFreeGreaterEq do not used with TriggerByAccqWhenFreeGreaterEq command.

```csharp
IRingBufferScaleMax<T> RollbackWhenFreeGreaterEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number to trigger.
 <br>The free resource collected must be greater than or equal to value.<br>Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity).

#### Returns

[IRingBufferScaleMax&lt;T&gt;](./ringbufferplus.iringbufferscalemax-1.md)

### <a id="methods-scalewhenfreelesseq"/>**ScaleWhenFreeLessEq(Nullable&lt;Int32&gt;)**

Condition to scale up to max capacity.
 <br>The free resource collected must be less than or equal to value.

```csharp
IRingBufferScaleMax<T> ScaleWhenFreeLessEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number to trigger.
 <br>The free resource collected must be less than or equal to value.<br>Default = Min (Min =  1, Max = Capacity).

#### Returns

[IRingBufferScaleMax&lt;T&gt;](./ringbufferplus.iringbufferscalemax-1.md)

### <a id="methods-triggerbyaccqwhenfreegreatereq"/>**TriggerByAccqWhenFreeGreaterEq(Nullable&lt;Int32&gt;)**

Condition to scale down to default capacity foreach accquire.
 <br>The free resource collected at aqccquire must be greater than or equal to value.<br>TriggerByAccqWhenFreeGreaterEq do not used with RollbackWhenFreeGreaterEq command.

```csharp
IRingBufferScaleMax<T> TriggerByAccqWhenFreeGreaterEq(Nullable<Int32> value)
```

#### Parameters

`value` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number to trigger.
 <br>The free resource collected must be greater than or equal to value.<br>Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)

#### Returns

[IRingBufferScaleMax&lt;T&gt;](./ringbufferplus.iringbufferscalemax-1.md)


- - -
[**Back to List Api**](./apis.md)