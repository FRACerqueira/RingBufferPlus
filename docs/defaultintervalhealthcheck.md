# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  SetIntervalHealthCheck

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval for performing the HealthCheck of a buffer item.

When not set,  default interval is the same value of DefaultValues.IntervalHealthcheck.

### Methods

```csharp
  IRingBuffer<T> SetIntervalHealthCheck(long mileseconds)
  IRingBuffer<T> SetIntervalHealthCheck(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
