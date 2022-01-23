# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # PolicyTimeoutAccquire

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Events**](events.md) |
[**Metrics**](metric.md)

## Documentation
Sets the timeout policy for acquiring items from the buffer. When not set, policy timeout acquisition is set to "MaximumCapacity".

### Policies Type(RingBufferPolicyTimeout)

- MaximumCapacity : Trigger the "Timeout CallBack" event only when the Ring Buffer instance is at maximum capacity.
- EveryTime : Trigger the "Timeout CallBack" event every time the maximum acquisition time limit is reached.
- UserPolicy : Trigger the "Timeout CallBack" event every time the user defined function is true.
- Ignore : Ignore the triggering of the "Timeout CallBack" event.

Notes: _**In all policies type the timeout count is performed.**_

### Methods

```csharp
   IRingBuffer<T> PolicyTimeoutAccquire(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
   IRingBuffer<T> PolicyTimeoutAccquireAsync(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, Task<bool>>? userpolicy = null)
``` 

### Exception

When set to "UserPolicy" and the user-function is null or when set user-function and the policy is not "UserPolicy"

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Events**](events.md) |
[**Metrics**](metric.md)

