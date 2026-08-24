# Usage: RabbitMQ channel pooling

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md) · [Elastic autoscale](usage-elastic-autoscale.md)

## When to use

You're publishing to RabbitMQ from multiple concurrent callers and want a pool of reusable `IChannel` instances instead of opening one channel per publish (or sharing a single channel unsafely across threads) — with the pool sized automatically to publish pressure.

## Minimal example

```csharp
var connectionRabbit = await connectionFactory.CreateConnectionAsync(cancellation);

static async Task<IChannel> ChannelFactory(IConnection connectionRabbit, CancellationToken cancellation) =>
    await connectionRabbit.CreateChannelAsync(cancellationToken: cancellation);

var rb = await RingBuffer<IChannel>.New("RabbitChannels")
    .Logger(logger)
    .Factory((token) => ChannelFactory(connectionRabbit, token))
    .ElasticCapacity(minCapacity: 5, maxCapacity: 20, target: 10, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(5))
    .BuildWarmupAsync(cancellation);

await using (var buffer = await rb.AcquireAsync(cancellation))
{
    if (buffer.Successful)
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("Test"));
        await buffer.Current!.BasicPublishAsync("", "log", body);
    }
}
```

This is the [elastic autoscale](usage-elastic-autoscale.md) pattern applied to `T = IChannel` — everything about capacity/scaling in that guide applies here unchanged; `RingBufferPlus` has no RabbitMQ-specific code path, only a generic `Factory` for whatever `T` you choose.

## What happens internally

Each `Factory` call opens one new `IChannel` on the shared `IConnection` you already created — the buffer pools *channels*, not *connections*. Publishing from many concurrent callers is what channel pooling is for: RabbitMQ channels are not safe to share across threads without external synchronization, and opening a channel per publish is comparatively expensive, so a pool amortizes that cost.

## Trade-offs / limitations

- The underlying `IConnection` is a single point of failure shared by every pooled channel — this library does not reconnect it for you. If the connection drops, every subsequent `Factory` call (on scale-up or item replacement) fails, and existing `AcquireAsync` calls degrade to `Successful = false` once their channels are exhausted or broken.
- `ChannelFactory` above passes `cancellation` straight through to `CreateChannelAsync`, which is what makes it well-behaved: this library now cancels that token at the per-item `FactoryTimeout` deadline, and `CreateChannelAsync` actually stops instead of continuing to open a channel nobody is waiting for anymore. A factory that drops the token has no such guarantee — a channel it opens after the library already gave up is discarded without being closed, leaking it on the broker connection.
- The backlog-reactive signal reacts to *publish-side* backpressure (real waiting callers), not to broker-side conditions (e.g. a slow consumer causing publisher confirms to back up) — those need their own monitoring.
- If channel creation on the broker fails only occasionally (transient, not the connection-lost case above), `Factory(value, timeout, maxConsecutiveFactoryFailures)` lets any creation batch (warmup fill or a scale-up) keep attempting the remaining items instead of abandoning the whole batch on the first failure — see the [autoscale guide](usage-elastic-autoscale.md) for the trade-off this introduces with `FactoryTimeout`. This applies to any scale-up, regardless of which signal triggered it (floor guard, backlog-reactive, or the Monitor) - all of them dispatch through the same `Factory`-calling path, and a partial result is kept silently either way: a tolerated failure still permanently loses that one slot (it is never retried), so `WarmupAsync()`/`BuildWarmupAsync()` still throws on any single unrecovered failure in the initial fill, however high you set this value - it does not make warmup itself more resilient to occasional broker hiccups, only a subsequent scale-up. (The exception type varies by design, not just by accident: if every attempt in the fill failed, the real factory exception is thrown as-is, for diagnostic value; `InvalidOperationException("RingBuffer did not reach initial capacity")` is thrown only when at least one item succeeded first - see Round 6's audit findings for the reasoning.)

## Common errors

- Sizing `maxCapacity` without checking RabbitMQ's `channel_max` connection-negotiated limit — exceeding it fails channel creation inside `Factory`, which surfaces as `Successful = false` on `AcquireAsync`, not as an immediate error at `Build` time.
- Reusing a `RingBufferValue<IChannel>`'s `Current` after disposing the wrapper — once disposed, the channel has been returned to the pool (or, if `Invalidate()` was called, discarded) and may already be handed to another caller.
