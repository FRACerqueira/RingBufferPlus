# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # MinScaler

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the minimum capacity of items in the buffer. When not set, MinScaler is the same initial capacity.

### Methods

```csharp
  IRingBuffer<T> MinScaler(int value)
``` 

### Exception

When MinScaler is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

