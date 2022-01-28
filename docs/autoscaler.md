# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  AutoScaler

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**RingBufferMetric**](metricclass.md)

## Documentation
Defines the function to run the AutoScaler for the buffer.

Should return new  target capacity for the buffer.

### Methods

```csharp
  IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> autoscaler)
  IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> autoscaler)
``` 

### Exception

When function is null.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**RingBufferMetric**](metricclass.md)
