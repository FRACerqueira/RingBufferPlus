# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  AutoScaler

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**AutoScaleCallback**](autoscalecallback.md) |
[**Metric**](metricclass.md)

## Documentation
Defines the function to run the AutoScaler for the buffer.

Should return new target capacity for the buffer. When resizing occurs, the [AutoScaleCallback](autoscalecallback.md) event will be triggered if configured

### Methods

```csharp
  IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> autoscaler)
  IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> autoscaler)
``` 

### Exception

When function is null.

If the user throws an exception it will be registered in the error event but it will not work in the next execution.

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**AutoScaleCallback**](autoscalecallback.md) |
[**Metric**](metricclass.md)


