# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # PolicyTimeoutAccquire

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Timeout CallBack**](timeoutcallback.md) |
[**RingBufferMetric**](metricclass.md)

## Documentation
Sets the timeout policy for acquiring items from the buffer. When not set, policy timeout acquisition is set to "EveryTime".

### Policies Type(RingBufferPolicyTimeout)

- Ignore : Ignore the triggering of the "Timeout CallBack" event.
- EveryTime : Trigger the "Timeout CallBack" event every time the maximum acquisition time limit is reached.
- MaximumCapacity : Trigger the "Timeout CallBack" event only when the Ring Buffer instance is at maximum capacity.
- UserPolicy : Trigger the "Timeout CallBack" event every time the user defined function is true.

Notes: _**In all policies type the timeout count is performed.**_

### Methods

```csharp
   IRingBuffer<T> SetPolicyTimeout(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
``` 

### Exception

When set to "UserPolicy" and the user-function is null or when set user-function and the policy is not "UserPolicy"

```csharp
  RingBufferException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Timeout CallBack**](timeoutcallback.md) |
[**RingBufferMetric**](metricclass.md)
