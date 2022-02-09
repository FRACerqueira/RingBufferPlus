# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  SetIntervalFailureState

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval when it enters a major fault state.

When not set,  default interval is the same value of DefaultValues.IntervalFailureState.

### Methods

```csharp
  IRingBuffer<T> SetIntervalFailureState(long mileseconds)
  IRingBuffer<T> SetIntervalFailureState(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
