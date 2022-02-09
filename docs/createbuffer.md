# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # CreateBuffer

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the initital capacity of items in the buffer and return new instance of IRingBuffer.

The properties InitialCapacity, MinimumCapacity and MaximumCapacity are given the same value.

### Methods

```csharp
  static IRingBuffer<T> CreateBuffer(int value = 2)
``` 

### Exception

When initital capacity is less than 2.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
