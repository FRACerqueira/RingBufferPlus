# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:RingBufferMetric 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# RingBufferMetric

Namespace: RingBufferPlus

Represents the Metric of RingBufferPlus.

```csharp
public struct RingBufferMetric
```

Inheritance [Object](https://docs.microsoft.com/en-us/dotnet/api/system.object) → [ValueType](https://docs.microsoft.com/en-us/dotnet/api/system.valuetype) → [RingBufferMetric](./ringbufferplus.ringbuffermetric.md)

## Properties

### <a id="properties-fromcapacity"/>**FromCapacity**

Current capacity.

```csharp
public int FromCapacity { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-metricdate"/>**MetricDate**

Date of metric .

```csharp
public DateTime MetricDate { get; }
```

#### Property Value

[DateTime](https://docs.microsoft.com/en-us/dotnet/api/system.datetime)<br>

### <a id="properties-tocapacity"/>**ToCapacity**

New capacity trigger.

```csharp
public int ToCapacity { get; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-trigger"/>**Trigger**

Source tigger.

```csharp
public SourceTrigger Trigger { get; }
```

#### Property Value

[SourceTrigger](./ringbufferplus.sourcetrigger.md)<br>

## Constructors

### <a id="constructors-.ctor"/>**RingBufferMetric()**

Create empty Metric of RingBufferPlus.

```csharp
RingBufferMetric()
```

### <a id="constructors-.ctor"/>**RingBufferMetric(SourceTrigger, Int32, Int32)**

Create Metric of RingBufferPlus.

```csharp
RingBufferMetric(SourceTrigger source, int fromcapacity, int tocapacity)
```

#### Parameters

`source` [SourceTrigger](./ringbufferplus.sourcetrigger.md)<br>
Source tigger.

`fromcapacity` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
Current capacity.

`tocapacity` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
New capacity trigger.


- - -
[**Back to List Api**](./apis.md)