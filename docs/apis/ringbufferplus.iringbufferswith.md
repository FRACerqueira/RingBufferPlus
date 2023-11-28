# <img align="left" width="100" height="100" src="../images/icon.png">RingBufferPlus API:IRingBufferSwith 

[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

[**Back to List Api**](./apis.md)

# IRingBufferSwith

Namespace: RingBufferPlus

Represents the commands to RingBufferPlus service.

```csharp
public interface IRingBufferSwith
```

## Methods

### <a id="methods-swithto"/>**SwithTo(ScaleMode)**

Swith to new capacity

```csharp
bool SwithTo(ScaleMode scaleMode)
```

#### Parameters

`scaleMode` [ScaleMode](./ringbufferplus.scalemode.md)<br>

#### Returns

True if scale changed, otherwise false


- - -
[**Back to List Api**](./apis.md)