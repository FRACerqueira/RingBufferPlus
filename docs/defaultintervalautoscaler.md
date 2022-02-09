# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  SetIntervalAutoScaler

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval to run the Auto-Scaler and warm-up to perform the first run. 

When not set, the default interval is the same as DefaultValues.IntervalScaler and the warm-up is zero.

### Methods

```csharp
  IRingBuffer<T> SetIntervalAutoScaler(long mileseconds,long? warmup = null)
  IRingBuffer<T> SetIntervalAutoScaler(TimeSpan value,TimeSpan? warmup = null)
``` 

### Exception

When values interval are less than or equal to zero.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
