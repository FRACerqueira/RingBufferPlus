# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBuffer<T> 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBuffer&lt;T&gt;

Namespace: RingBufferPlus

Represents the commands to RingBufferPlus.

```csharp
public interface IRingBuffer<T>
```

#### Type Parameters

`T`<br>
Type of buffer.

## Methods

### <a id="methods-accquiretimeout"/>**AccquireTimeout(TimeSpan)**

Timeout to accquire buffer. Default value is 30 seconds.

```csharp
IRingBuffer<T> AccquireTimeout(TimeSpan value)
```

#### Parameters

`value` [TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan)<br>
The timeout for acquiring a value from the buffer.

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

### <a id="methods-bufferhealth"/>**BufferHealth(Func&lt;T, Boolean&gt;, Nullable&lt;TimeSpan&gt;)**

Check buffer health with each acquisition or after timeout

```csharp
IRingBuffer<T> BufferHealth(Func<T, Boolean> value, Nullable<TimeSpan> timeout)
```

#### Parameters

`value` Func&lt;T, Boolean&gt;<br>
The handler to health.

`timeout` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The timeout for checking buffer integrity when there is no acquisition. Default value is 30 seconds

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

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

### <a id="methods-capacity"/>**Capacity(Int32)**

Default capacity of ring buffer.

```csharp
IRingBuffer<T> Capacity(int value)
```

#### Parameters

`value` [Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>
Initial capacity.

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

### <a id="methods-factory"/>**Factory(Func&lt;CancellationToken, T&gt;, Nullable&lt;TimeSpan&gt;, Nullable&lt;TimeSpan&gt;)**

Factory to create an instance in ring buffer.
 <br>Executes asynchronously.

```csharp
IRingBuffer<T> Factory(Func<CancellationToken, T> value, Nullable<TimeSpan> timeout, Nullable<TimeSpan> idleRetryError)
```

#### Parameters

`value` Func&lt;CancellationToken, T&gt;<br>
The handler to factory.

`timeout` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The timeout for build. Default value is 10 seconds.

`idleRetryError` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The delay time for retrying when a build fails. Default value is 5 seconds.

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

### <a id="methods-logger"/>**Logger(ILogger)**

The Logger
 <br>Default value is ILoggerFactory.Create (if any) with category euqal name of ring buffer

```csharp
IRingBuffer<T> Logger(ILogger value)
```

#### Parameters

`value` ILogger<br>
.

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

### <a id="methods-onerror"/>**OnError(Action&lt;ILogger, RingBufferException&gt;)**

Extension point to log a error.
 <br>Executes asynchronously.

```csharp
IRingBuffer<T> OnError(Action<ILogger, RingBufferException> errorhandler)
```

#### Parameters

`errorhandler` [Action&lt;ILogger, RingBufferException&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.action-2)<br>
he handler to log error.

#### Returns

[IRingBuffer&lt;T&gt;](./ringbufferplus.iringbuffer-1.md).

### <a id="methods-scaleunit"/>**ScaleUnit(ScaleMode, Nullable&lt;Int32&gt;, Nullable&lt;TimeSpan&gt;)**

Swith to scale definitions commands with scale mode and sampling unit/base timer for return buffer-free resource (Median colledted samples).
 <br>basetimer/numbersamples must be greater or equal than 100ms.

```csharp
IRingBufferScaleCapacity<T> ScaleUnit(ScaleMode unit, Nullable<Int32> numbersamples, Nullable<TimeSpan> basetimer)
```

#### Parameters

`unit` [ScaleMode](./ringbufferplus.scalemode.md)<br>
Scale unit type.
 <br>When  is Manual the 'numbersamples' and 'basetimer' parameters will be ignored<br>When  is Slave the 'numbersamples' and 'basetimer' parameters will be ignored

`numbersamples` [Nullable&lt;Int32&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
Number of samples collected.Default value is basetimer/10. Default basetimer is 60.

`basetimer` [Nullable&lt;TimeSpan&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.nullable-1)<br>
The [TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan) interval to colleted samples.Default value is 60 seconds.

#### Returns

IRingBufferScaleCapacity&lt;T&gt;


- - -
[**Back to List Api**](./apis.md)