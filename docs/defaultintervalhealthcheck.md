# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  DefaultIntervalHealthCheck

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval for performing the HealthCheck of a buffer item.

When not set,  default interval is the same value of DefaultValues.IntervalHealthcheck.

### Methods

```csharp
  IRingBuffer<T> DefaultIntervalHealthCheck(long mileseconds)
  IRingBuffer<T> DefaultIntervalHealthCheck(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

