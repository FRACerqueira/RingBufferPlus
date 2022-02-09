# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # InitialBuffer

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**CreateBuffer**](createbuffer.md) |
[**MaxBuffer**](maxbuffer.md) 

## Documentation
Sets the Initial Buffer capacity of items in the buffer.

When not set, InitialBuffer is the same value of [CreateBuffer](createbuffer.md).

if InitialBuffer is greater than MaxBuffer, the value of [MaxBuffer](maxbuffer.md) will be updated to have the same value.

### Methods

```csharp
  IRingBuffer<T> InitialBuffer(int value)
``` 

### Exception

When InitialBuffer is less than to 2. 

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**CreateBuffer**](createbuffer.md) |
[**MaxBuffer**](maxbuffer.md) 

