# Usage: ASP.NET Core / generic host dependency injection

[**Back to README**](../../README.md) · See also: [Concepts: thread-safety and DI](concepts.md#thread-safety-and-dependency-injection)

## When to use

You're hosting the buffer inside an ASP.NET Core app or any generic-host application and want it registered as a singleton, resolved via constructor injection like any other service, with warmup driven by the host's own startup/shutdown lifecycle instead of manual `BuildWarmupAsync`/`DisposeAsync` calls in `Main`.

## Minimal example

```csharp
// Program.cs
builder.Services.AddRingBuffer<int>("MyBuffer", (ringbuf, services) =>
{
    var applifetime = services.GetService<IHostApplicationLifetime>();
    return ringbuf
        .Factory((_) => Task.FromResult(10))
        .ElasticCapacity(5, 2, 7)
        .Build(applifetime!.ApplicationStopping);
});

var app = builder.Build();

await app.WarmupRingBufferAsync<int>("MyBuffer");

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

`WarmupRingBufferAsync<T>(app, name, token?)` looks up the registered singleton by name among every `IRingBufferService<T>` for that `T`, then calls `WarmupAsync` on it — with the given `token`, or `IHostApplicationLifetime.ApplicationStopping` if none is passed. It throws `ArgumentNullException` if no buffer with that name and type was registered — it does not silently no-op.

Because the DI container owns the singleton's lifetime, disposal on host shutdown (`await app.StopAsync()` / process exit) calls `DisposeAsync()` on it automatically — you don't need to call it yourself, unlike the console-app pattern in the other usage guides.

## Trade-offs / limitations

- If your buffer needs manual switching (`SwitchToAsync`), pattern-match the injected `IRingBufferService<T>`: `if (service is IRingBufferManualScaleService<int> manual) { ... }`. The pattern-match itself always succeeds; it is the subsequent call to `SwitchToAsync` that throws `InvalidOperationException` if the underlying builder went through `AutoScaleAcquireFault` (see [ADR007](../adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)).
- Multiple `T`s each registered under `AddRingBuffer<T>` resolve independently — `WarmupRingBufferAsync<T>` only searches among services registered for that specific `T`, so a name collision across different `T`s is not a conflict.

## Common errors

- Calling `Build(...)` instead of `BuildWarmupAsync(...)` inside the `AddRingBuffer` callback and then forgetting to call `app.WarmupRingBufferAsync<T>(...)` afterward — the buffer is registered but never filled, so the first real `AcquireAsync` pays the full factory cost inline.
- Passing a mismatched `buffername` to `WarmupRingBufferAsync<T>` (typo, or wrong `T`) — this throws `ArgumentNullException` at startup rather than failing silently, which is a deliberate fix over the v4 behavior (see the `CHANGELOG.md` "Breaking changes v5.0.0" entry).
