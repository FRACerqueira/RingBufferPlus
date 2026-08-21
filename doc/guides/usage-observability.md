# Usage: observability (metrics and tracing)

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md) · [ADR008](../adr/ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)

## When to use

You're running RingBufferPlus in production and want the pool's internal state — current capacity, acquire faults, scale-up/scale-down events, acquire latency — visible in whatever you already use to observe the rest of the system (Grafana/Prometheus, Application Insights, Jaeger, or anything else an OpenTelemetry Collector can feed). This is on by default and needs no builder call to enable — it's covered here so you know what to point an exporter at, not because there's a switch to flip.

## Minimal example

RingBufferPlus emits metrics via `System.Diagnostics.Metrics.Meter` and traces via `System.Diagnostics.ActivitySource`, both named `"RingBufferPlus"`. It takes no dependency on OpenTelemetry itself — wire up whichever OpenTelemetry .NET SDK exporter you already use, the same way you would for `System.Net.Http` or ASP.NET Core's own built-in instrumentation:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("RingBufferPlus") /* + whichever exporter you already use */)
    .WithTracing(t => t.AddSource("RingBufferPlus") /* + whichever exporter you already use */);
```

The two calls that matter here are `AddMeter("RingBufferPlus")` and `AddSource("RingBufferPlus")` — both are OpenTelemetry SDK APIs that subscribe to any `System.Diagnostics.Metrics.Meter`/`ActivitySource` by name, and RingBufferPlus already emits under that name whether or not anything is listening. No RingBufferPlus-specific package is required on your side either. The exporter call (Prometheus, OTLP, Application Insights, or anything else) is deliberately left as a comment above — which one you add, and its exact method name, depends on your OpenTelemetry SDK version and target backend, and is not something this guide can keep in sync; consult the OpenTelemetry .NET docs for whichever exporter you use.

## What happens internally

Every `RingBufferManager<T>` owns its own `Meter` and `ActivitySource` instance — not one shared static instance for the whole process — but both are constructed with the same constant name, `"RingBufferPlus"`, so a single exporter subscription sees every buffer in the process. Every individual buffer is disambiguated by a `buffer.name` tag/attribute on every metric and every span, using the same `Name` you passed to `RingBuffer<T>.New(name)`.

**Metrics:**

| Instrument | Kind | Tags | Description |
|---|---|---|---|
| `ringbufferplus.acquire.duration` | Histogram\<double\> (seconds) | `buffer.name`, `acquire.success`, `acquire.timed_out`, `acquire.cancelled` | Duration of every `AcquireAsync` call — the same value already available per-call as `RingBufferValue<T>.ElapsedTime`. On a failed row, `acquire.timed_out` distinguishes a genuine `AcquireTimeout` expiring from an ordinary shutdown/caller-cancellation; `acquire.cancelled` is `true` only when the *caller's own* token ended the call (mirrors the Acquire activity's own tags below). |
| `ringbufferplus.acquire.faults` | Counter\<long\> | `buffer.name` | Count of `AcquireAsync` calls that timed out with nothing available. |
| `ringbufferplus.capacity.current` | ObservableGauge\<int\> | `buffer.name` | Current capacity, read live whenever your exporter's collection interval polls it. |
| `ringbufferplus.scale.operations` | Counter\<long\> | `buffer.name`, `direction` (`up`/`down`), `trigger` (`manual`/`auto`), `success`, `cancelled` | Count of scale-up/scale-down attempts, including ones that failed, timed out, or were cancelled by an ordinary `DisposeAsync()` racing them — check `success` before reading this as "capacity actually changed N times", and `cancelled` before reading a `success=false` **scale-up** row as a genuine factory/broker problem (see the Logging section below for the same distinction on the log side). A scale-**down**'s `cancelled` is always `false` and a `success=false` row there is never a factory/broker problem — see the Tracing paragraph below for why. The initial warmup fill is **not** counted here — see below. |
| `ringbufferplus.scale.duration` | Histogram\<double\> (seconds) | `buffer.name`, `direction`, `success`, `cancelled` | Duration of scale-up/scale-down attempts, whether or not they succeeded. |

**Tracing:** one `Activity` named `"RingBufferPlus.Acquire"` per `AcquireAsync` call (tagged `buffer.name`, `success`, `timed_out`, and `cancelled` when the caller's own token — not a timeout — ended the call; its `ActivityStatusCode` is `Ok` unless `timed_out` is `true`, in which case it's `Error` — an ordinary shutdown or caller cancellation is not a health signal, only a genuine `AcquireTimeout` expiring is), and one named `"RingBufferPlus.Scale"` per scale operation (tagged `buffer.name`, `direction`, `trigger`, `cancelled`, and its `ActivityStatusCode` set to `Error` only on a genuine failed/timed-out attempt — `Ok` if it succeeded, or if it was cancelled by an ordinary `DisposeAsync()` racing it) — both correlate naturally with the rest of your request trace if `AcquireAsync` happens inside a traced request. A scale-**down** that only partially reaches its target (not enough idle items available right now — see [ADR001](../adr/ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)'s opportunistic, non-blocking design) is always `Ok`/`cancelled=false` too, never `Error`: it can't genuinely fail or be cancelled the way a scale-up can, since it never calls `Factory` and never observes a token — check `success` (not the status) if you need to know whether it fully reached its target.

**`cancelled` means something different on each of these two signals** — on Acquire, it's `true` only when *your own* `cancellation` token ended the call (an ordinary shutdown leaves it `false`, distinguishable via `timed_out` instead); on Scale, it's `true` when an ordinary `DisposeAsync()` (the buffer's own shutdown, not a caller-supplied token) cancelled an in-flight scale-**up** — a scale-down's `cancelled` is always `false`, even when `DisposeAsync()` raced it (see the Tracing paragraph above for why). Each is documented accurately where it's tagged, but don't assume the two mean the same thing if you're aggregating across both.

Two things that are easy to miss because they follow directly from what counts as an "acquire" or a "scale operation" here, not from any special-casing:

- **Warmup emits nothing.** The initial fill produces no `scale.*` metric, no `"RingBufferPlus.Scale"` activity, and no `acquire.*` signal either — it isn't a scale operation in the manual/auto sense those describe, and it doesn't go through `AcquireAsync`. `capacity.current` still reflects the buffer correctly the moment your collector's polling interval fires, since that gauge just reads live state, but there is no *event* marking "warmup finished" in either metrics or traces.
- **`HeartBeat` acquires count too, but don't drive autoscale.** Every `PulseHeartBeat` tick internally acquires the item your callback inspects, so it shows up in `acquire.duration`/`acquire.faults` and produces its own `"RingBufferPlus.Acquire"` span, indistinguishable from a caller-initiated acquire by tag. If your dashboard cares about the difference, there is currently no tag for it — correlate by volume/interval against your configured `PulseHeartBeat` instead. A heartbeat acquire that times out is still counted in `acquire.faults`, but it is deliberately exempt from ever *triggering* an `AutoScaleAcquireFault` scale-up — only genuine caller demand counts toward that budget. Watching `acquire.faults` to predict an autoscale reaction can therefore be misleading on a buffer with both `HeartBeat` and `AutoScaleAcquireFault` configured.

## Trade-offs / limitations

- Both APIs are "pay for play," but "near-zero" is not "zero" — measured on this project's own benchmark harness (`benchmarks/RingBufferPlus.Benchmarks/ObservabilityOverheadBenchmarks.cs`), comparing against the actual pre-Phase-7 code (not just against itself):
  - Before this feature existed: ~279ns, 568 B per `AcquireAsync`/release cycle (`AcquireThroughputBenchmarks`, measured against the commit before ADR008).
  - After, with nothing subscribed: ~312ns, 592 B — about 33ns (~12%) more, from the instrumentation call sites themselves still running their internal "is anyone listening" checks even when the answer is no.
  - After, with a `MeterListener`/`ActivityListener` actually attached: ~587ns, 1216 B — roughly 1.9x the unobserved cost.
  All three numbers are nanoseconds-per-call; they are negligible next to any real factory work (network calls, database connections, RabbitMQ channels) the pool exists to manage, but the ~12% unobserved delta is real, not a rounding artifact, and worth knowing if you're benchmarking RingBufferPlus itself rather than a workload built on top of it.
- This is always on — there is no builder method to disable it. It costs nothing to leave alone if you don't use it; there is nothing to configure either.
- No RingBufferPlus-specific NuGet package is needed on either end: the emitting side needs none (see [ADR008](../adr/ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)), and the consuming side just needs whatever OpenTelemetry SDK/exporter you'd use for any other `System.Diagnostics.DiagnosticSource`-based library.

## Logging: a normal shutdown is not the same signal as a genuine failure

This guide otherwise covers `Meter`/`ActivitySource` only — `Logger`/`OnError` (see [the heartbeat guide](usage-heartbeat.md) and [the background logger guide](usage-background-logger.md)) are a separate signal, but this distinction is worth knowing if you alert on logged errors: an operation still in flight when `DisposeAsync()` runs (an ordinary, clean shutdown racing a background operation) logs informationally via `Logger`/`OnError` instead of at error level. This applies to three independent operations, each with its own message and error-level exception type when the cause is a genuine failure instead:

- A scale-up or item-replacement factory call: `"ScaleUp cancelled by shutdown..."`/`"Replacement cancelled by shutdown."` vs. `LogError(TimeoutException(...))` on a genuine per-item or overall `FactoryTimeout` expiring.
- The initial warmup fill: `"Warmup cancelled by shutdown before reaching initial capacity."` vs. `LogError(InvalidOperationException("RingBuffer did not reach initial capacity"))` on a genuine failure to reach capacity.
- A `HeartBeat` callback still running when `DisposeAsync()` races it: `"Heart Beat cancelled by shutdown, callback still running - deferring dispose."` vs. `LogError(TimeoutException("Timeout Heart Beat"))` when the callback instead exceeded its own `pulse` budget.

A fourth message is a different kind of case - not "shutdown vs. failure," but "shutdown completed, but a resource is not yet confirmed disposed": if `DisposeAsync()` had to wait for an orphaned `HeartBeat` callback's deferred resource disposal (the third case above) and that callback still hasn't finished once `DisposeAsync()`'s own `pulse`-bounded grace period elapses, it logs `"DisposeAsync did not wait for {N} orphaned heartbeat callback(s) still running past the grace period..."` at `LogWarning` - a step above the informational level of the three cases above, since this one is a real, indeterminate-duration resource leak, not a shutdown-vs-failure ambiguity - and returns anyway - the resource(s) will still be disposed later if/when the callback(s) actually finish, just not before this `DisposeAsync()` call already returned.

If your alerting treats every logged error from this library as a factory/broker/callback health signal, a clean restart racing any of these three should no longer trip it — and if you previously saw such an error disappear after upgrading, this is why.

## Common errors

- Expecting `AddMeter("RingBufferPlus")`/`AddSource("RingBufferPlus")` to only pick up one specific buffer — they subscribe by name, not by instance; every buffer in the process with that default name shares the subscription. Use the `buffer.name` tag/attribute to split them apart in your dashboard/query, not a separate subscription per buffer.
- Looking for a metric/span the moment a buffer is *built* — nothing is emitted until the first real `AcquireAsync`/scale operation happens; `capacity.current` is the only signal available immediately (as soon as your collector's polling interval fires), since it's a live gauge, not an event.
