# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:RingBufferValue<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# RingBufferValue&lt;T&gt;

Namespace: RingBufferPlus

Represents acquired the value in the buffer.

```csharp
public class RingBufferValue<T> : System.IDisposable
```

#### Type Parameters

`T`<br>
Type of buffer.

Inheritance [Object](https://docs.microsoft.com/en-us/dotnet/api/system.object) → [RingBufferValue&lt;T&gt;](./ringbufferplus.ringbuffervalue-1.md)<br>
Implements [IDisposable](https://docs.microsoft.com/en-us/dotnet/api/system.idisposable)

## Properties

### <a id="properties-current"/>**Current**

The buffer value.

```csharp
public T Current { get; }
```

#### Property Value

T<br>

### <a id="properties-elapsedtime"/>**ElapsedTime**

Elapsed time to acquire the value.

```csharp
public TimeSpan ElapsedTime { get; }
```

#### Property Value

[TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>

### <a id="properties-name"/>**Name**

Name of RingBuffer.

```csharp
public string Name { get; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-successful"/>**Successful**

Successful Acquire.

```csharp
public bool Successful { get; }
```

#### Property Value

[Boolean](https://docs.microsoft.com/en-us/dotnet/api/system.boolean)<br>

## Constructors

### <a id="constructors-.ctor"/>**RingBufferValue(Int32)**

Create empty RingBufferValue.

```csharp
public RingBufferValue(int diffCapacity)
```

#### Parameters

`diffCapacity` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="constructors-.ctor"/>**RingBufferValue(String, TimeSpan, Boolean, T, Action&lt;RingBufferValue&lt;T&gt;&gt;)**

Create RingBufferValue.

```csharp
public RingBufferValue(string name, TimeSpan elapsedTime, bool succeeded, T value, Action<RingBufferValue<T>> turnback)
```

#### Parameters

`name` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
Name of RingBuffer.

`elapsedTime` [TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>
Elapsed time to acquire the value.

`succeeded` [Boolean](https://docs.microsoft.com/en-us/dotnet/api/system.boolean)<br>
Successful Acquire.

`value` T<br>
The buffer value.

`turnback` Action&lt;RingBufferValue&lt;T&gt;&gt;<br>
The action handler to turn back buffer when disposed.

## Methods

### <a id="methods-dispose"/>**Dispose(Boolean)**

Turnback value to buffer.

```csharp
protected void Dispose(bool disposing)
```

#### Parameters

`disposing` [Boolean](https://docs.microsoft.com/en-us/dotnet/api/system.boolean)<br>
Disposing.

### <a id="methods-dispose"/>**Dispose()**

Turnback value to buffer.

```csharp
public void Dispose()
```

### <a id="methods-invalidate"/>**Invalidate()**

Invalidates the return of the value to the buffer. Another instance will be created.
 <br>This command will be ignored if the return was unsuccessful.

```csharp
public void Invalidate()
```


- - -
[**Back to List Api**](./apis.md)