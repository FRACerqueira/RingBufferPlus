# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  MetricsReport

[**Main**](index.md#help) | 
[**State**](currentstate.md) | 
[**Apis**](index.md#apis)

## Documentation 
Defines the action/fucntion that performs the Metric-Report.

### Methods

```csharp
  IRingBuffer<T> MetricsReport(Action<RingBufferMetric, CancellationToken> report)
  IRingBuffer<T> MetricsReportAsync(Func<RingBufferMetric, CancellationToken, Task> report)
``` 

### Exception

When action/fucntion is null.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**State**](currentstate.md) | 
[**Apis**](index.md#apis)

