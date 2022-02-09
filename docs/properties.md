# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # Properties

[**Main**](index.md#help) | 
[**State**](currentstate.md) | 
[**Apis**](index.md#apis)

## Documentation

Ring buffer properties.

### Properties - (All Properties are ReadOnly)

- Alias 
	-  Alias for the Ringbuffer instance.
- CurrentState 
	-  Ring buffer [CurrentState](currentstate.md) class.
- InitialCapacity 
	-  Initial capacity of items in the buffer.
- MinimumCapacity
	-  Minimum capacity of items in the buffer.
- MaximumCapacity
	-  Maximum capacity of items in the buffer.
- IdleAccquire
	-  Waiting time between acquisition attempts when the acquisition has no items available.
- IntervalHealthCheck
	-  Interval for performing the HealthCheck of a buffer item.
- IntervalAutoScaler
	-  Interval for performing the AutoScaler of a buffer item.
- IntervalReport
	-  Interval for performing the Metric-Report of a buffer item.
- TimeoutAccquire
	-  Timeout for acquiring items in the buffer.
- IntervalFailureState
	-  Waiting interval when entering a failed state before executing the next attempt.
- PolicyTimeout
	-  Policy TimeoutPolicy timeout when a timeout occurs.
- DefaultLogLevel
	-  Default log level to write to logging provider.
- HasLogging
	-  Flag indicating that logging is enabled.
- HasReport
	-  Flag indicating that Metric-Report is enabled.
- HasPolicyTimeout
	-  Flag indicating that policy timeout is enabled.
- HasHealthCheck
	-  Flag indicating that health-check is enabled.
- HasAutoScaler
	-  Flag indicating that user AutoScaler is enabled.
- HasLinkedFailureState
	-  Flag indicating that external Failure-State is enabled.

### Links
[**Main**](index.md#help) | 
[**State**](currentstate.md) | 
[**Apis**](index.md#apis)