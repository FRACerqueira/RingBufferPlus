# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  DefaultIntervalOpenCircuit

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation
Sets the default interval to wait for a new open circuit check.

When not set,  default interval is the same value of DefaultValues.IntervalOpenCircuit.

### Methods

```csharp
  IRingBuffer<T> DefaultIntervalOpenCircuit(long mileseconds)
  IRingBuffer<T> DefaultIntervalOpenCircuit(TimeSpan value)
``` 

### Exception

When default interval is less than or equal to zero.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
