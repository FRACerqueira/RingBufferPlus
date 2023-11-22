# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:RingBufferException 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# RingBufferException

Namespace: RingBufferPlus

Represents the Exception by RingBufferPlus.

```csharp
public class RingBufferException : System.Exception, System.Runtime.Serialization.ISerializable
```

Inheritance [Object](https://docs.microsoft.com/en-us/dotnet/api/system.object) → [Exception](https://docs.microsoft.com/en-us/dotnet/api/system.exception) → [RingBufferException](./ringbufferplus.ringbufferexception.md)<br>
Implements [ISerializable](https://docs.microsoft.com/en-us/dotnet/api/system.runtime.serialization.iserializable)

## Properties

### <a id="properties-data"/>**Data**

```csharp
public IDictionary Data { get; }
```

#### Property Value

[IDictionary](https://docs.microsoft.com/en-us/dotnet/api/system.collections.idictionary)<br>

### <a id="properties-helplink"/>**HelpLink**

```csharp
public string HelpLink { get; set; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-hresult"/>**HResult**

```csharp
public int HResult { get; set; }
```

#### Property Value

[Int32](https://docs.microsoft.com/en-us/dotnet/api/system.int32)<br>

### <a id="properties-innerexception"/>**InnerException**

```csharp
public Exception InnerException { get; }
```

#### Property Value

[Exception](https://docs.microsoft.com/en-us/dotnet/api/system.exception)<br>

### <a id="properties-message"/>**Message**

```csharp
public string Message { get; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-nameringbuffer"/>**NameRingBuffer**

Name of ring buffer

```csharp
public string NameRingBuffer { get; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-source"/>**Source**

```csharp
public string Source { get; set; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-stacktrace"/>**StackTrace**

```csharp
public string StackTrace { get; }
```

#### Property Value

[String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>

### <a id="properties-targetsite"/>**TargetSite**

```csharp
public MethodBase TargetSite { get; }
```

#### Property Value

[MethodBase](https://docs.microsoft.com/en-us/dotnet/api/system.reflection.methodbase)<br>

## Constructors

### <a id="constructors-.ctor"/>**RingBufferException()**

Create empty RingBufferException..

```csharp
public RingBufferException()
```

### <a id="constructors-.ctor"/>**RingBufferException(String, String)**

Create RingBufferException.

```csharp
public RingBufferException(string name, string message)
```

#### Parameters

`name` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
Name of ring buffer.

`message` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
The message exception.

### <a id="constructors-.ctor"/>**RingBufferException(String, String, Exception)**

Create RingBufferException with inner exception.

```csharp
public RingBufferException(string name, string message, Exception inner)
```

#### Parameters

`name` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
Name of ring buffer.

`message` [String](https://docs.microsoft.com/en-us/dotnet/api/system.string)<br>
The message exception.

`inner` [Exception](https://docs.microsoft.com/en-us/dotnet/api/system.exception)<br>
The inner exception.


- - -
[**Back to List Api**](./apis.md)