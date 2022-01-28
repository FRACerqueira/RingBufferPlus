# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # DefaultTimeoutAccquire

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default Timeout for  accquire items in the buffer.

When not set,  default Timeout is the same value of DefaultValues.TimeoutAccquire.

### Methods

```csharp
  IRingBuffer<T> DefaultTimeoutAccquire(long mileseconds)
  IRingBuffer<T> DefaultTimeoutAccquire(TimeSpan value)
``` 

### Exception

When default Timeout is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)


