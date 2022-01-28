# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  Factory

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Defines the function to create an instance in the buffer.

### Methods

```csharp
  IRingBuffer<T> Factory(Func<CancellationToken, T> value)
  IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value)
``` 

### Exception

When function is null.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
