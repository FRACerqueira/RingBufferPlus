# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  DefaultIntervalReport

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval for Metrics-Report.

When not set,  default interval is the same value of DefaultValues.IntervalReport.

### Methods

```csharp
  IRingBuffer<T> DefaultIntervalReport(long mileseconds)
  IRingBuffer<T> DefaultIntervalReport(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)