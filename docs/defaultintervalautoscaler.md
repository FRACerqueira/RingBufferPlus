# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  DefaultIntervalAutoScaler

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval to run Auto-Scaler.

When not set,  default interval is the same value of DefaultValues.IntervalScaler.

### Methods

```csharp
  IRingBuffer<T> DefaultIntervalAutoScaler(long mileseconds)
  IRingBuffer<T> DefaultIntervalAutoScaler(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)