# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # MaxBuffer

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the maximum capacity of items in the buffer. When not set, MaxBuffer is the same value of [CreateBuffer](createbuffer.md).

### Methods

```csharp
  IRingBuffer<T> MaxScaler(int value)
``` 

### Exception

When MaxBuffer is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

