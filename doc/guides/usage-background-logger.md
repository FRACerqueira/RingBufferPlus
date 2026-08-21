# Usage: background logger

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

By default, every log call (`Logger`) and error report (`OnError`) executes synchronously, inline, on whichever thread/task triggered it — a warmup step, a scale operation, a heartbeat tick. If your `ILogger` sink is slow (network-backed, buffered file I/O under contention), that latency leaks into the operation that triggered the log. `BackgroundLogger()` moves the actual write off that path.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .BackgroundLogger()
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);
```

`BackgroundLogger()` only has an effect when `Logger` and/or `OnError` is also configured — with neither set, there is nothing to log and no background task is started.

## What happens internally

With `BackgroundLogger()` enabled, every debug/warning/error log call enqueues a message onto a dedicated `Channel<LogMessageBackground>` instead of calling into `ILogger` directly; a separate consumer task drains that channel and performs the actual `ILogger` write (or `OnError` invocation for errors) off the calling path. Without it, the same log call writes synchronously, in place, before the caller's operation continues.

## Trade-offs / limitations

- Log ordering across different operations is preserved (single consumer, FIFO channel), but the *timing* of when a message actually reaches your sink is decoupled from when the triggering event happened — don't rely on wall-clock proximity between an operation and its log line if you enable this.
- `DisposeAsync` drains and completes the log channel as part of shutdown, so no background log message is silently dropped on a graceful dispose — but a message enqueued after the channel is completed (e.g. from a task racing shutdown) is simply not written. This loss is specific to `BackgroundLogger(true)`'s fire-and-forget queue, not "inherent to any bounded-lifetime background consumer": the same late message under the default synchronous logging mode still reaches your `Logger`/`OnError`, just delayed rather than lost.

## Common errors

- Enabling `BackgroundLogger()` without setting `Logger` or `OnError` — harmless, but the option does nothing without a sink to write to.
- Expecting a log line to already be visible in your sink immediately after the operation that produced it returns — with `BackgroundLogger()`, there's an unspecified (usually small) delay before the consumer task catches up.
