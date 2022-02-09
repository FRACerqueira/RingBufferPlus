# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # DefaultTimeoutAccquire

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default timeout for acquiring items in the buffer and the wait time between attempts.

When not set,  default Timeout is the same value of DefaultValues.TimeoutAccquire and DefaultValues.WaitTimeAvailable.

### Methods

```csharp
  IRingBuffer<T> SetTimeoutAccquire(long mileseconds, long? idle = null)
  IRingBuffer<T> SetTimeoutAccquire(TimeSpan value, TimeSpan? idle = null)
``` 

### Exception

When values are less than or equal to zero.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
