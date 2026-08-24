# Usage: ASP.NET Core / generic host dependency injection

[**Back to README**](../../README.md) · See also: [Concepts: thread-safety and DI](concepts.md#thread-safety-and-dependency-injection)

## When to use

You're hosting the buffer inside an ASP.NET Core app or any generic-host application and want it registered as a singleton, resolved via constructor injection like any other service, with warmup driven automatically by the host's own startup/shutdown lifecycle instead of manual `BuildWarmupAsync`/`DisposeAsync` calls in `Main`.

## Minimal example

```csharp
// Program.cs
builder.Services.AddRingBuffer<int>("MyBuffer", (ringbuf, services) =>
{
    var applifetime = services.GetService<IHostApplicationLifetime>();
    return ringbuf
        .Factory((_) => Task.FromResult(10))
        .ElasticCapacity(2, 7, 5)
        .Build(applifetime!.ApplicationStopping);
});

var app = builder.Build();

// No explicit warmup call needed - AddRingBuffer<T> already registered an IHostedService
// alongside the pool (ADR007V03), which runs during app.Run()'s own host startup.

app.Run();
```

```csharp
// a controller, or any constructor-injected class
public class WeatherForecastController(IRingBufferService<int> ringBufferService) : ControllerBase
{
    private readonly IRingBufferService<int> _ringBufferService = ringBufferService;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        await using var buffer = await _ringBufferService.AcquireAsync(token);
        return buffer.Successful ? Ok(buffer.Current) : StatusCode(503);
    }
}
```

## What happens internally

`AddRingBuffer<T>` registers a singleton factory that, on first resolution, hands your callback an `IRingBufferBuilder<T>` plus the `IServiceProvider` — use the latter to pull in `IHostApplicationLifetime` (for the lifetime token) or any other registered dependency your `Factory` needs. Only `IRingBufferService<T>` is registered — regardless of the more specific type your callback's builder chain returns, injecting `IRingBufferManualScaleService<T>` directly is not possible; see the trade-off below for manual switching.

`AddRingBuffer<T>` also registers an `IHostedService` for this specific `buffername` (ADR007V03) - its `StartAsync` resolves its own buffer by a DI key (`buffername`) and calls `WarmupAsync` on it, using `StartAsync`'s own token. Resolving by key, rather than enumerating and filtering every `IRingBufferService<T>` registered for that `T`, means a broken `userfunc` for one `buffername` cannot fault another, unrelated buffer's startup - a configuration typo in one buffer no longer risks keeping the whole host from starting. It throws `InvalidOperationException` if no buffer with that name and type was registered - it does not silently no-op. Warmup is therefore automatic on host start; there is no longer a separate opt-in call (`WarmupRingBufferAsync` was removed - calling it is now a compile error, not a runtime no-op).

Because the DI container owns the singleton's lifetime, disposal on host shutdown (`await app.StopAsync()` / process exit) calls `DisposeAsync()` on it automatically — you don't need to call it yourself, unlike the console-app pattern in the other usage guides. `DisposeAsync()` bounds each pooled item's own `Dispose()`/`DisposeAsync()` by `pulse` while draining the pool - this applies regardless of whether `HeartBeat` is configured at all, so budget for it against `HostOptions.ShutdownTimeout` either way. If a `HeartBeat` callback is also configured and happens to be orphaned from a previous timeout when this runs, `DisposeAsync()` can take up to an extra `pulse` interval on top of that to return - see the [heartbeat guide](usage-heartbeat.md).

## Trade-offs / limitations

- If your buffer needs manual switching (`SwitchToAsync`), pattern-match the injected `IRingBufferService<T>`: `if (service is IRingBufferManualScaleService<int> manual) { ... }`. The pattern-match itself always succeeds; it is the subsequent call to `SwitchToAsync` that throws `InvalidOperationException` if the underlying builder used `FixedCapacity` instead of `ElasticCapacity` (see [ADR007V03](../adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md) - every elastic buffer supports manual switching now, there is no separate autoscale-only mode to exclude it).
- Multiple `T`s each registered under `AddRingBuffer<T>` resolve independently — the DI key (`buffername`) each hosted service resolves by is scoped to that specific `T` (a closed generic type), so a name collision across different `T`s is not a conflict.
- **Two buffers registered under the *same* `T` with different `buffername`s are not independent for plain constructor injection.** `AddRingBuffer<T>` always registers as `IRingBufferService<T>` regardless of `buffername` - the name only matters to its own hosted service's lookup. A class that injects `IRingBufferService<T>` directly (like the controller above) silently gets whichever registration was added *last*, with no error.
  Do **not** work around this with `IEnumerable<IRingBufferService<T>>` plus your own name-based lookup - resolving that `IEnumerable` constructs *every* registered buffer of that `T`, including ones you didn't ask for, so a broken `userfunc` for an unrelated buffer still faults your resolution (Round 2 pre-release audit finding - the isolation `AddRingBuffer<T>`'s hosted service gets from its DI key does not extend to this path).
  Instead, inject the specific buffer you want with `[FromKeyedServices(buffername)]`: `public MyController([FromKeyedServices("MyBuffer")] IRingBufferService<int> buffer)`. This resolves directly by the same DI key the hosted service already uses, so it only ever constructs that one buffer, regardless of how many other same-`T` buffers are registered or whether their `userfunc`s are broken.
- Warmup now always runs at host start - there is no way to opt out of it via `AddRingBuffer<T>` itself. If you need to construct a buffer without warming it up automatically, build it directly via `RingBuffer<T>.New(...)` instead of going through `AddRingBuffer<T>`.

## Common errors

- Registering a `buffername` that a typo elsewhere in your composition root doesn't match — the hosted service throws `InvalidOperationException` at host startup rather than failing silently.
