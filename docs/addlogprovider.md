# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus #  AddLogProvider

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) 

## Documentation
Add log provider to Ring Buffer to generate run information to user log.

When logging is enabled, exceptions and warnings are generated even with callback events enabled.

### RingBufferLogLevel Type

Set default log level to setter/information/trace. Error and warning log levels are not affected.

- Trace (Default)
- Debug
- Information

### Methods

```csharp
  IRingBuffer<T> AddLogProvider(ILoggerFactory value, RingBufferLogLevel defaultlevel = = RingBufferLogLevel.Trace)
```

### Exception

No Exception. When ILoggerFactory is null, the log generate is ignored.

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis)
