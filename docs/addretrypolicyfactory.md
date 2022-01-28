# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # AddRetryPolicyFactory

[**Main**](index.md#help) | 
[**Apis**](index.md#apis)

## Documentation

Adds a retry policy to the factory function. The retry policy expects to receive a [3rd party component - Polly](https://github.com/App-vNext/Polly)

### Methods

```csharp
  IRingBuffer<T> AddRetryPolicyFactory(RetryPolicy<T> policy)
```

### Exception

When policy is null.

```csharp
  RingBufferFatalException
``` 

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
