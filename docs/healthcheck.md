# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  HealthCheck

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Defines the function to run the HealthCheck for an item in the buffer. 

Should return true when successful, otherwise false.

### Methods

```csharp
  IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value)
  IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value)
``` 

### Exception

When function is null.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
