# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  SetIntervalReport

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval for Metrics-Report. 

When not set,  default interval is the same value of DefaultValues.IntervalReport.

### Methods

```csharp
  IRingBuffer<T> SetIntervalReport(long mileseconds)
  IRingBuffer<T> SetIntervalReport(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
