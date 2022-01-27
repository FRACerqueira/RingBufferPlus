# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # MinBuffer

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**CreateBuffer**](createbuffer.md)

## Documentation
Sets the minimum capacity of items in the buffer. When not set, MinBuffer is the same value of [CreateBuffer](createbuffer.md).

### Methods

```csharp
  IRingBuffer<T> MinBuffer(int value)
``` 

### Exception

When MinBuffer is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**CreateBuffer**](createbuffer.md)

