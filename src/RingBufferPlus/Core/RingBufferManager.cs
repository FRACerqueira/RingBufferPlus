// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

// Design note (ADR001/ADR005): all mutable scale state (_currentCapacity, fault counters,
// samples) belongs exclusively to the single consumer loop (RunEngineAsync). No other thread
// mutates it, so no lock or semaphore is needed. Callers only post commands into an unbounded
// Channel<EngineCommand> and, optionally, await a completion signal.
//
// Two deliberate simplifications versus v4, both authorized by ADR006 (no compatibility
// obligation) and consistent with ADR001's "correctness by construction over blocking dances":
//  - AcquireAsync never blocks on an in-flight scale operation. It always reads directly from
//    the available-items channel, which blocks only until an item exists, regardless of
//    LockWhenScaling. LockWhenScaling controls exactly one thing: whether SwitchToAsync's
//    caller awaits the scale operation's completion before returning.
//  - AcquireDelayAttempts is removed: a Channel-based read has no polling loop to pace.
//  - Warmup is a Lazy<Task> (ExecutionAndPublication), so concurrent callers share one attempt
//    instead of each starting their own (v4's retry path was broken: a failed Startup() left
//    _WarmupRunning stuck true forever). A failed attempt is no longer cached forever (ADR011):
//    calling WarmupAsync() again after a failure installs and runs a fresh attempt (CAS on
//    _warmup). AcquireAsync and SwitchToAsync's implicit warmup trigger (EnsureWarmupAsync)
//    never auto-retries - it just reports the latest attempt's outcome, so ordinary traffic
//    against a broken factory can't turn into a retry storm. Retrying only happens when a
//    caller explicitly calls WarmupAsync().
//
// Observability (ADR008): _meter and _activitySource are per-instance, not static, though both
// share the name "RingBufferPlus" - a listener subscribed to that name still sees every live
// buffer, but disposing one buffer's Meter/ActivitySource never silences another's. Both APIs
// are "pay for play": with no listener attached, Add/Record/StartActivity calls cost almost
// nothing, so they always run - there's no opt-in/opt-out on the builder. Warmup's own
// MoveToCapacityAsync call carries no scaleTrigger: the initial fill isn't a "scale operation"
// in the sense the scale.* metrics describe.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed class RingBufferManager<T> : IRingBufferManualScaleService<T>
    {
        #region fields

        private readonly CancellationTokenSource _lifetime;
        private readonly Channel<T> _availableItems = Channel.CreateUnbounded<T>();
        private readonly Channel<EngineCommand> _commands = Channel.CreateUnbounded<EngineCommand>();
        private Lazy<Task> _warmup;
        private readonly Task _engineTask;
        // Cached once instead of converting the method group to a delegate on every successful
        // AcquireAsync (the compiler can't cache that conversion itself, since TurnbackAsync is an
        // instance method and the conversion captures `this`). A small allocation removed from the
        // per-request path, not the per-scale-operation path.
        private readonly Func<RingBufferValue<T>, ValueTask> _turnbackDelegate;

        // Boxing `true`/`false` as the `object?` tag value for a metric/activity call allocates a
        // fresh box every time, even though only two values ever occur. Reusing these boxes avoids
        // that, but only on the AcquireCoreAsync success path - the one path measured as hot. The
        // failure catch blocks and the scale.* tags below are cold paths with no measured benefit.
        private static readonly object BoxedTrue = true;
        private static readonly object BoxedFalse = false;

        // Monitor's sliding demand window (ADR001V03/ADR003V03), bounded to SamplesCount.
        // Engine-thread-only: only ProcessTick and the scale-completion cleanup paths touch it.
        private readonly List<int> _samples = [];

        // Monitor (ADR001V03/ADR003V03): whether the last tick that actually ran found demand
        // keeping pace with or exceeding capacity ("active"). demand = CurrentCapacity - idle +
        // waiting, so "active" (demand >= CurrentCapacity) simplifies to waiting >= idle. That
        // covers genuine backlog (idle=0, waiting>0), but also ordinary full utilization with
        // nobody queued (idle=0, waiting=0, since 0 >= 0). So a pool sitting exactly at its current
        // capacity - a common, ordinary state, not just MaxCapacity with real backlog - pauses the
        // Monitor's sample collection for as long as that lasts.
        //
        // This is accepted, not a bug: the backlog-reactive signal already takes over the instant
        // demand would exceed capacity, so the Monitor doing nothing at exactly-at-capacity is
        // redundant with it anyway. Narrowing this to a waiting-only condition would be a real
        // algorithm change, and it would need the same decision-quality simulation ADR003V03 was
        // validated with - which AutoScaleAlgorithmComparison doesn't currently support, since it
        // feeds a synthetic demand sequence directly instead of a separate idle/waiting split.
        //
        // _samples is cleared unconditionally after any scale operation (see MoveToCapacityAsync's
        // and FactoryBatchCompleted's finally blocks), but only a scale operation clears it, and
        // MonitorDeadband gates whether one fires at all. During a genuinely steady period - demand
        // stable, capacity already matching it, every computed target inside the deadband - no
        // scale operation fires, so nothing clears the window: it fills to SamplesCount, one sample
        // per tick, then slides, exactly as intended for ordinary operation. If demand then drops
        // during that steady period, the new low samples have to outvote a full window of stale
        // high ones before the percentile/trend catches up - see numberSamples' own doc for what
        // that lag looks like at the shipped default. This field's clearing (and the scale-op
        // clears above) only shorten that lag when a real burst/backlog episode, not a quiet steady
        // period, precedes the drop.
        //
        // Engine-thread-only, like _currentCapacity itself - only ProcessTick reads or writes it.
        private bool _monitorActive;

        private readonly Meter _meter = new("RingBufferPlus");
        private readonly ActivitySource _activitySource = new("RingBufferPlus");
        private readonly Histogram<double> _acquireDuration;
        private readonly Counter<long> _acquireFaults;
        private readonly Counter<long> _scaleOperations;
        private readonly Histogram<double> _scaleDuration;
        private readonly Counter<long> _heartbeatInvalidations;

        private Task? _heartbeatTask;
        private Task? _sampleTickTask;

        // Deferred dispose continuations from an orphaned heartbeat callback's still-hanging
        // item dispose (see the timeout branches in RunHeartbeatAsync below) - added only from
        // RunHeartbeatAsync's own loop, so by the time _heartbeatTask (awaited in DisposeAsync
        // before this list is snapshotted) has completed, no more entries can arrive.
        //
        // Uses a lock plus a plain list rather than a ConcurrentBag<Task>: the single writer here
        // (RunHeartbeatAsync, which resumes on an arbitrary pool thread after each await, not a
        // fixed one) never actually needed the multi-producer thread affinity a ConcurrentBag
        // optimizes for. Nothing else writes here, and the real correctness guarantee comes from
        // awaiting _heartbeatTask, not from the collection type. This also closes a hazard the old
        // take/re-add pattern had: an exception between the drain loop and the re-add loop could
        // silently lose entries already taken out. A single lock scope can't leave that gap.
        private readonly object _pendingHeartbeatDisposalsGate = new();
        private readonly List<Task> _pendingHeartbeatDisposals = new();

        // Volatile, not a plain bool: written once, synchronously, as DisposeAsync's first
        // instruction, then read from other threads (AcquireCoreAsync, SwitchToAsync,
        // WarmupAsync/WarmupCoreAsync) with no other memory barrier guaranteeing they see it.
        private volatile bool _disposed;
        private int _disposeGuard;
        private int _currentCapacity;
        private volatile bool _scaling;

        // Manual pin (ADR007V03): while set in the future, a successful SwitchToAsync's target
        // substitutes for the Monitor's predictive output - Tick is skipped entirely (see the Tick
        // case's own comment), the same way it already is while _scaling is true. So the Monitor
        // neither observes nor overrides the pinned capacity for as long as the pin lasts. A pin
        // never suppresses EvaluateFloorGuard or EvaluateBacklogReactive (ADR007V03) - both keep
        // running on their own independent triggers, untouched by this field.
        //
        // Engine-thread-only, like _currentCapacity: only the Switch case writes it, only the Tick
        // case reads it.
        private DateTime? _pinExpiresAt;

        // Backlog-reactive signal (ADR001V03): count of callers currently blocked in
        // AcquireCoreAsync waiting for an item. Mutated via Interlocked from any caller thread
        // (unlike _currentCapacity, this is a shared counter, not sole-owner state), and read by
        // the engine thread in EvaluateBacklogReactive.
        private int _waitingCount;

        // Floor guard (ADR001V03): the instant CurrentCapacity < MinCapacity was first detected.
        // Engine-thread-only, like _currentCapacity. Set once on first detection and left
        // untouched across retries - only cleared once the breach actually resolves - so the grace
        // window (FactoryTimeout, reused per the ADR) measures time since the breach started, not
        // since the most recent retry.
        private DateTime? _floorBreachDetectedAt;
        // The instant the "below minimum" report last fired, or null if it never has for the
        // current breach - see FloorGuardDecision.ShouldReportNow. Cleared alongside
        // _floorBreachDetectedAt once the breach resolves, so a later, unrelated breach starts its
        // own fresh report cadence.
        private DateTime? _floorBreachLastReportedAt;

        // Creator (ADR001V03): the in-flight background scale-up batch (DispatchScaleUp), if any.
        // Only the engine thread (single consumer) writes it, and only one batch runs at a time -
        // Switch rejects, and EvaluateBacklogReactive skips, a new dispatch while _scaling is true.
        // DisposeAsync reads this only after _engineTask has already been awaited to completion
        // (so no further write can race it), and awaits it too, so a batch still in flight at
        // shutdown still gets to record its telemetry and resolve its caller before DisposeAsync
        // returns.
        //
        // A new dispatch can overwrite this field before the old batch's Task.Run lambda has fully
        // returned: the engine clears _scaling - unblocking a new dispatch - as soon as it
        // processes that batch's FactoryBatchCompleted command, which the lambda posts as its last
        // statement before returning. Only the newest batch's task is ever tracked, never a list.
        // This is safe because by the time a batch posts its completion command, it has nothing
        // observable left to do - so an overwritten reference is never still-working code that
        // DisposeAsync fails to wait for.
        private Task? _factoryBatchTask;

        // Removal (ADR001V03): the in-flight background scale-down disposal batch
        // (DispatchScaleDown), if any - same shape and guarantees as _factoryBatchTask above, and
        // covered by the same "only one batch of either kind in flight at a time" invariant via
        // _scaling. Isolated from the engine thread for the same reason Creator's creation
        // execution already is: Dispose()/DisposeAsync() on a real connection can block on I/O just
        // like a factory call can. Before this role existed, RemoveItemsAsync's disposal (bounded
        // by PulseHeartBeat, the same grace-period mitigation DisposeItemsDefensivelyAsync uses)
        // ran inline on the engine's single-consumer thread, so even a bounded hang there delayed
        // every other queued command (a floor-guard replenishment, an unrelated ReplaceOne, ...)
        // for up to that same grace period. DisposeAsync awaits this the same way, alongside
        // _factoryBatchTask.
        private Task? _removalBatchTask;

        // Creator (ADR001V03): a simple growing backoff after consecutive genuine factory
        // failures - self-protection against hammering a broken factory, not a circuit breaker.
        // Persisted on the manager, not scoped to one CreateItemsAsync/CreateSingleReplacementAsync
        // call, so a factory that stays broken across several separate attempts (repeated
        // floor-guard cycles, repeated backlog-reactive requests, etc.) gets throttled
        // progressively over time - that cross-call repetition, not one batch's own bounded
        // concurrency, is what this guards against. Reset to zero by any genuine success,
        // incremented by any genuine (non-cancellation) failure. Not exposed as builder
        // configuration - "simple", per the ADR, means a small fixed policy, not a new tunable.
        private int _factoryFailureStreak;
        private static readonly TimeSpan FactoryBackoffBase = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan FactoryBackoffMax = TimeSpan.FromSeconds(5);

        #endregion

        #region configuration (set by RingBufferBuilder via object initializer)

        public required string Name { get; init; }

        public int Capacity { get; init; }

        public int MinCapacity { get; init; }

        public int MaxCapacity { get; init; }

        public TimeSpan FactoryTimeout { get; init; }

        public byte MaxConsecutiveFactoryFailures { get; init; }

        /// <summary>
        /// Maximum number of concurrent factory calls when creating several items at once (ADR001V03,
        /// Creator role). This is the thundering-herd mitigation: it stops a large batch (warmup,
        /// scale-up, or floor-guard replenishment) from flooding a struggling downstream with
        /// simultaneous creation attempts. Defaults to <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/>,
        /// so a direct object-initializer construction that omits it (e.g. in tests) never silently
        /// deadlocks CreateItemsAsync's gate at a zero-permit semaphore.
        /// </summary>
        public int MaxConcurrentFactoryCalls { get; init; } = RingBufferDefault.MaxConcurrentFactoryCalls;

        public TimeSpan PulseHeartBeat { get; init; }

        public TimeSpan SamplesBase { get; init; }

        public int SamplesCount { get; init; }

        /// <summary>
        /// True for an elastic pool (ADR001V03/ADR007V03). The backlog-reactive signal and Monitor
        /// are unconditionally active when this is true, and inactive when it's false (Monitor: not
        /// even started; backlog-reactive: gated off) - a fixed pool has no elastic range for
        /// either to move within. The floor guard is different: it is never gated by this property
        /// (see <see cref="FloorGuardDecision"/>'s own class remarks) and applies uniformly in every
        /// mode, including fixed capacity, since a failed replacement can shrink even a fixed
        /// pool's capacity below its single value. There is no further "automatic vs. manual" split
        /// within an elastic pool: <see cref="SwitchToAsync(ScaleSwitch, TimeSpan)"/> is a temporary
        /// pin over the same always-on Monitor, not an alternative mode.
        /// </summary>
        public bool Elastic { get; init; }

        /// <summary>
        /// The Monitor's predictive autoscale algorithm parameters (ADR003V03). Defaulted here,
        /// not just on the builder, so a direct object-initializer construction that omits them
        /// (e.g. in tests) still runs the algorithm with the ADR's own defaults - the same reasoning
        /// as <see cref="MaxConcurrentFactoryCalls"/>'s default.
        /// </summary>
        public double MonitorPercentileP { get; init; } = RingBufferDefault.MonitorPercentileP;

        public double MonitorSafetyBuffer { get; init; } = RingBufferDefault.MonitorSafetyBuffer;

        public double MonitorHorizon { get; init; } = RingBufferDefault.MonitorHorizon;

        public int MonitorDeadband { get; init; } = RingBufferDefault.MonitorDeadband;

        public TimeSpan AcquireTimeout { get; init; }

        public bool LockWhenScaling { get; init; }

        public ILogger? Logger { get; init; }

        public Action<Exception>? ErrorHandler { get; init; }

        public Func<T, bool>? BufferHeartBeat { get; init; }

        public required Func<CancellationToken, Task<T>> Factory { get; init; }

        #endregion

        #region IRingBufferService

        public bool IsMinCapacity => CurrentCapacity == MinCapacity;

        public bool IsMaxCapacity => CurrentCapacity == MaxCapacity;

        public bool IsInitCapacity => CurrentCapacity == Capacity;

        public int CurrentCapacity => Volatile.Read(ref _currentCapacity);

        #endregion

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2016:Forward the 'CancellationToken' parameter to methods", Justification = "ByDesign")]
        public RingBufferManager(CancellationToken lifetimecancellation)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimecancellation);
            _warmup = new Lazy<Task>(WarmupCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
            _turnbackDelegate = TurnbackAsync;

            _acquireDuration = _meter.CreateHistogram<double>("ringbufferplus.acquire.duration", unit: "s", description: "Duration of AcquireAsync calls, in seconds.");
            _acquireFaults = _meter.CreateCounter<long>("ringbufferplus.acquire.faults", description: "Count of AcquireAsync calls that timed out with no item available.");
            _scaleOperations = _meter.CreateCounter<long>("ringbufferplus.scale.operations", description: "Count of scale-up/scale-down operations, tagged by buffer.name, direction, trigger, target, success, and cancelled.");
            _scaleDuration = _meter.CreateHistogram<double>("ringbufferplus.scale.duration", unit: "s", description: "Duration of scale-up/scale-down operations, in seconds, tagged by buffer.name, direction, trigger, target, success, and cancelled.");
            // A HeartBeat's "unhealthy" verdict - the most common way a HeartBeat-configured pool
            // actually operates - had no signal of its own: the pump's one log line fires
            // identically whether the verdict is healthy or not. Without this counter, an operator
            // watching telemetry alone couldn't tell a pool that's constantly cycling items apart
            // from a healthy one.
            _heartbeatInvalidations = _meter.CreateCounter<long>("ringbufferplus.heartbeat.invalidations", description: "Count of HeartBeat callback verdicts that invalidated the inspected item for replacement, tagged by buffer.name.");
            // RingBufferBuilder.BuildCore constructs this manager via object-initializer syntax, so
            // Name/Capacity/etc. (all `required`, none with an inline default) are only assigned by
            // the compiler after this constructor already returns. But the gauge callback above
            // closes over `Name`/`CurrentCapacity`, and _engineTask starts below, both before that
            // assignment happens. A MeterListener polling this ObservableGauge in that brief window
            // would read `buffer.name: null` - a spurious telemetry reading, not a state corruption
            // (CurrentCapacity's backing field is already a valid 0 at this point, same as before
            // warmup). Left as-is: fixing it for real means decoupling engine-start/gauge-
            // registration from the builder's object-initializer pattern (e.g. taking Name/Capacity
            // as constructor parameters instead), which changes how every RingBufferManager<T> is
            // built - too big a change for a narrow window with only a cosmetic failure mode. Same
            // "keep as-is, document as a trade-off" shape as the CTS/timer decision in
            // AcquireCoreAsync below.
            _meter.CreateObservableGauge("ringbufferplus.capacity.current",
                () => new Measurement<int>(CurrentCapacity, new KeyValuePair<string, object?>("buffer.name", Name)),
                description: "Current capacity of the buffer.");

            _engineTask = Task.Run(RunEngineAsync);
        }

        public ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default) =>
            AcquireCoreAsync(countsTowardFaultBudget: true, cancellation);

        // The heartbeat pump's own internal acquire (see RunHeartbeatAsync) is a health check,
        // not consumer demand - counting its timeout toward the autoscale fault budget would be
        // a self-inflicted false signal. Genuine external demand still faults normally through
        // the public AcquireAsync above; this only exempts the heartbeat's own pulse.
        private ValueTask<RingBufferValue<T>> AcquireForHeartbeatAsync(CancellationToken cancellation) =>
            AcquireCoreAsync(countsTowardFaultBudget: false, cancellation);

        // _disposed is set synchronously as DisposeAsync's very first step, but
        // _lifetime.Dispose() only runs at the end of its finally, after awaiting in-flight
        // engine/heartbeat/sample-tick work. That leaves a TOCTOU window between a caller's
        // ObjectDisposedException.ThrowIf(_disposed, this) guard passing and its next
        // _lifetime.Token access. CancellationTokenSource.Token's getter (and
        // CreateLinkedTokenSource over it) throws ObjectDisposedException on an already-disposed
        // source regardless - the right exception type, but with ObjectName pointing at
        // CancellationTokenSource instead of this manager, which reads confusingly like an
        // unrelated internal failure during an otherwise-ordinary shutdown. Since _disposed flips
        // to true strictly before _lifetime is ever disposed, catching that specific case here and
        // re-throwing through the same guard restores the identity a caller already expects.
        private CancellationToken LifetimeToken()
        {
            try
            {
                return _lifetime.Token;
            }
            catch (ObjectDisposedException)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                throw;
            }
        }

        private async ValueTask<RingBufferValue<T>> AcquireCoreAsync(bool countsTowardFaultBudget, CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var warmupStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                await EnsureWarmupAsync().ConfigureAwait(false);
            }
            catch
            {
                // EnsureWarmupAsync's Lazy<Task> caches a failed initial warmup attempt (ADR011 -
                // only an explicit WarmupAsync() call installs a fresh attempt and retries). Every
                // implicit call through here keeps rethrowing that same cached exception. Rather
                // than rethrowing before any telemetry even starts, this records a full
                // acquire.duration row and Activity for it, so a sustained warmup failure stays
                // visible even though every call keeps genuinely failing. It deliberately does NOT
                // call LogError here - that already happened once, inside WarmupCoreAsync, the
                // first time this same failure was reached. Repeating it on every implicit call
                // would turn ordinary acquire traffic against a known-broken buffer into a
                // log-volume storm, the same failure mode ADR011 already avoided for retries.
                //
                // Without a dedicated tag, this row's success/timed_out/cancelled combination
                // (false/false/false) would be identical to an ordinary DisposeAsync() racing an
                // in-flight call below. A metrics-only consumer (no ActivityListener attached, a
                // perfectly normal setup) couldn't tell "the buffer is permanently broken, every
                // call is failing" apart from "a benign, transient shutdown race". So
                // acquire.warmup_failed/warmup_failed is `true` only here, `false` on every other
                // row/span - the same always-present contract as the other three tags.
                using var failedWarmupActivity = _activitySource.StartActivity("RingBufferPlus.Acquire");
                failedWarmupActivity?.SetTag("buffer.name", Name);
                _acquireDuration.Record(Stopwatch.GetElapsedTime(warmupStartTimestamp).TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", false),
                    new KeyValuePair<string, object?>("acquire.timed_out", false),
                    new KeyValuePair<string, object?>("acquire.cancelled", false),
                    new KeyValuePair<string, object?>("acquire.warmup_failed", true));
                failedWarmupActivity?.SetTag("success", false);
                failedWarmupActivity?.SetTag("timed_out", false);
                failedWarmupActivity?.SetTag("cancelled", false);
                failedWarmupActivity?.SetTag("warmup_failed", true);
                failedWarmupActivity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }

            using var activity = _activitySource.StartActivity("RingBufferPlus.Acquire");
            activity?.SetTag("buffer.name", Name);

            var startTimestamp = Stopwatch.GetTimestamp();
            // Measured at ~416 of ~600 B/op on the uncontended fast path: this CTS/timer is built
            // and torn down even when the immediate TryRead below succeeds and the timeout is never
            // consulted. It's left as eager construction on purpose, not made lazy (built only in
            // the else branch, once the fast path has failed). Making it lazy would mean hoisting
            // timeoutCts/linked out of that branch's scope, so the catch blocks below can still
            // check timeoutCts.IsCancellationRequested - replacing the clean `using var` pattern
            // with manual disposal in a finally, and replacing the pre-fast-path
            // `!linked.IsCancellationRequested` check with one against the raw cancellation/
            // _lifetime tokens instead, in a method whose cancellation correctness already rests on
            // several subtle interactions between those tokens. The allocation is real, but not
            // the actual bottleneck for this library's use case (pooling network/DB resources, where
            // Factory's own I/O dominates by orders of magnitude) - not worth risking one of those
            // bug classes to remove it. Same "keep as-is" trade-off as the 4x amplification decision
            // in CreateItemsAsync.
            using var timeoutCts = new CancellationTokenSource(AcquireTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, LifetimeToken(), cancellation);
            var isWaiting = false;
            try
            {
                T item;
                // linked.IsCancellationRequested is checked before the fast path, so an already (or
                // immediately) cancelled token still surfaces its genuine cancellation via
                // ReadAsync below. A bare TryRead observes no token at all, and would otherwise let
                // an available idle item mask a caller's own cancellation just because the pool
                // happened to be non-empty.
                if (!linked.IsCancellationRequested && _availableItems.Reader.TryRead(out var immediate))
                {
                    item = immediate;
                }
                else
                {
                    // Backlog-reactive signal (ADR001V03): a caller that can't be served right away
                    // is genuine, real-time demand - report it to the Orchestrator now, before
                    // AcquireTimeout has any chance to elapse (the old fault-count trigger only
                    // reacted after that timeout already happened). Same reasoning as the fault
                    // path: the heartbeat's own internal acquire is a health check, not consumer
                    // demand, so it must not count here either. Gating just the Backlog() dispatch
                    // below isn't enough on its own, because _waitingCount is also read directly by
                    // EvaluateBacklogReactive/ProcessTick - a heartbeat blocked waiting would still
                    // inflate it even though it never triggered anything.
                    if (countsTowardFaultBudget)
                    {
                        Interlocked.Increment(ref _waitingCount);
                        isWaiting = true;
                        if (Elastic)
                        {
                            _commands.Writer.TryWrite(EngineCommand.Backlog());
                        }
                    }
                    item = await _availableItems.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                    // Decrement right here, not in the shared finally below: this caller stops
                    // waiting the instant it actually has an item, not after the telemetry and
                    // RingBufferValue construction that follow. Otherwise a later
                    // EvaluateBacklogReactive call (e.g. from FactoryBatchCompleted, on another
                    // thread) reading _waitingCount in between would still count this already-
                    // served caller as backlog. Narrowing that window is what keeps
                    // EvaluateBacklogReactive's documented approximation small in practice, not
                    // just tolerated in theory.
                    if (isWaiting)
                    {
                        Interlocked.Decrement(ref _waitingCount);
                        isWaiting = false;
                    }
                }
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                // acquire.timed_out/acquire.cancelled are always emitted (false here), not only on
                // the failure rows below - same tag-contract reasoning as DispatchScaleDown's
                // scale.* tags. Otherwise a consumer filtering acquire.timed_out="false" would get
                // zero rows for every successful acquire, since the label simply wouldn't exist,
                // instead of the "calls without a timeout" rows they'd expect.
                _acquireDuration.Record(elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", BoxedTrue),
                    new KeyValuePair<string, object?>("acquire.timed_out", BoxedFalse),
                    new KeyValuePair<string, object?>("acquire.cancelled", BoxedFalse),
                    new KeyValuePair<string, object?>("acquire.warmup_failed", BoxedFalse));
                activity?.SetTag("success", BoxedTrue);
                activity?.SetTag("timed_out", BoxedFalse);
                // "cancelled" belongs on every span, not just the caller-cancellation one below -
                // leaving it absent instead of false would make this row indistinguishable from
                // that other case, the same tag-contract gap already avoided for the
                // acquire.duration histogram's own "cancelled" key.
                activity?.SetTag("cancelled", BoxedFalse);
                activity?.SetTag("warmup_failed", BoxedFalse);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new RingBufferValue<T>(Name, elapsed, true, item, _turnbackDelegate);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                var timedOut = timeoutCts.IsCancellationRequested;
                if (timedOut)
                {
                    // Fault-count-based autoscale triggering is retired - ADR001V03's backlog-
                    // reactive signal replaces it. By the time a genuine AcquireTimeout is even
                    // possible, this same caller already reported itself as backlog the moment it
                    // started waiting (see the TryRead fast path above), well before this point.
                    // acquire.faults below is a pure diagnostic counter now, unrelated to triggering.
                    LogWarning("RingBuffer without resource");
                    _acquireFaults.Add(1, new KeyValuePair<string, object?>("buffer.name", Name));
                }
                // acquire.timed_out mirrors the activity's own "timed_out" tag: without it, this
                // histogram's failed rows can't be told apart from an ordinary
                // shutdown/caller-cancellation.
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                _acquireDuration.Record(elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", false),
                    new KeyValuePair<string, object?>("acquire.timed_out", timedOut),
                    new KeyValuePair<string, object?>("acquire.cancelled", false),
                    new KeyValuePair<string, object?>("acquire.warmup_failed", false));
                activity?.SetTag("success", false);
                activity?.SetTag("timed_out", timedOut);
                // Same tag-contract gap as the success path above - "cancelled" belongs on every
                // row, not just the caller-cancellation one below.
                activity?.SetTag("cancelled", false);
                activity?.SetTag("warmup_failed", false);
                // Only a genuine timeout is a health signal worth an Error status - reaching this
                // catch without timedOut means _lifetime (an ordinary shutdown) is what ended the
                // wait instead.
                activity?.SetStatus(timedOut ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
                return new RingBufferValue<T>(Name, elapsed, false, default!, null);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The caller's own token fired, not a timeout/disposal - this rethrows unchanged
                // (see the sibling catch above), but the activity/duration must still record an
                // outcome before it does, or a trace shows an outcome-less span for this call.
                _acquireDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", false),
                    new KeyValuePair<string, object?>("acquire.timed_out", false),
                    new KeyValuePair<string, object?>("acquire.cancelled", true),
                    new KeyValuePair<string, object?>("acquire.warmup_failed", false));
                activity?.SetTag("success", false);
                activity?.SetTag("timed_out", false);
                activity?.SetTag("cancelled", true);
                activity?.SetTag("warmup_failed", false);
                // The caller choosing to cancel their own call is not a buffer health problem.
                activity?.SetStatus(ActivityStatusCode.Ok);
                throw;
            }
            finally
            {
                if (isWaiting)
                {
                    Interlocked.Decrement(ref _waitingCount);
                }
            }
        }

        public async Task<bool> SwitchToAsync(ScaleSwitch value, TimeSpan pinDuration)
        {
            if (!Elastic)
            {
                throw new InvalidOperationException("Manual scale switching is not available: the buffer has a fixed capacity (see ADR007).");
            }
            if (pinDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pinDuration), pinDuration, "pinDuration must be greater than TimeSpan.Zero.");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                await EnsureWarmupAsync().ConfigureAwait(false);

                var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Switch(value, pinDuration, accepted, completion), LifetimeToken()).ConfigureAwait(false);

                // Both bounded by _lifetime.Token: if this command loses its race against disposal
                // and is abandoned unread in the channel, this must not hang forever waiting for
                // signals nobody will ever send.
                var wasAccepted = await accepted.Task.WaitAsync(LifetimeToken()).ConfigureAwait(false);
                if (!wasAccepted)
                {
                    return false;
                }
                if (!LockWhenScaling)
                {
                    // Nobody awaits completion.Task on this unlocked path - if the engine loop
                    // later resolves it via TrySetException on a genuine scale failure, that fault
                    // would become an unobserved task exception once this TCS is garbage-collected.
                    // Merely attaching a continuation does not mark the exception observed -
                    // reading .Exception inside it is what actually does that.
                    _ = completion.Task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return true;
                }
                return await completion.Task.WaitAsync(LifetimeToken()).ConfigureAwait(false);
            }
            // Only an ordinary shutdown of this buffer's own lifetime resolves to a plain `false`
            // here. A raw OperationCanceledException/TaskCanceledException thrown by the factory
            // itself (e.g. an HttpClient/gRPC/DB-driver's own unrelated internal timeout) must not
            // be swallowed into an indistinguishable, exception-less `false` - it falls through
            // this guard and propagates to the caller like any other genuine factory exception.
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return false;
            }
        }

        public Task WarmupAsync(CancellationToken cancellation = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // If the current attempt already failed, install a fresh one so this call retries
            // instead of rethrowing the same cached failure forever (ADR011). Only an explicit
            // WarmupAsync() call retries - EnsureWarmupAsync (AcquireAsync/SwitchToAsync's implicit
            // trigger) never replaces a faulted attempt on its own.
            var current = Volatile.Read(ref _warmup);
            if (current.IsValueCreated && current.Value.IsFaulted)
            {
                var fresh = new Lazy<Task>(WarmupCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
                var previous = Interlocked.CompareExchange(ref _warmup, fresh, current);
                // CompareExchange always returns the value that was there before the exchange: if
                // it still matches `current`, our `fresh` won and is now installed; otherwise another
                // caller already installed its own fresh attempt first - use that one instead.
                current = ReferenceEquals(previous, current) ? fresh : previous;
            }

            return current.Value.WaitAsync(cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
            _disposed = true;

            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                _commands.Writer.TryComplete();

                // If a warmup was already in flight, let it unwind first (it observes cancellation
                // and returns or throws) before snapshotting which background pumps to await -
                // otherwise a pump task WarmupCoreAsync assigns after our snapshot would never be
                // awaited below.
                // _warmup can have been replaced (ADR011's retry path) by a racing WarmupAsync()
                // call, so read the current reference rather than assuming it never changes.
                var warmupSnapshot = Volatile.Read(ref _warmup);
                if (warmupSnapshot.IsValueCreated)
                {
                    try
                    {
                        await warmupSnapshot.Value.ConfigureAwait(false);
                    }
                    catch
                    {
                        //ignore: disposal is in progress, warmup's own outcome no longer matters
                    }
                }

                var pending = new List<Task> { _engineTask };
                if (_heartbeatTask is not null) pending.Add(_heartbeatTask);
                if (_sampleTickTask is not null) pending.Add(_sampleTickTask);

                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Disposal must be best-effort and never throw, regardless of how a background
                    // pump ended - but an unexpected fault here is still worth surfacing through the
                    // configured logger/ErrorHandler rather than being fully silent. No separate
                    // OperationCanceledException catch is needed: RunEngineAsync, RunHeartbeatAsync,
                    // and RunSampleTickAsync each already fully own their own OperationCanceledException
                    // at their own outermost level, so none of the tasks in `pending` can ever
                    // propagate one here. This catch-all covers whatever else might.
                    LogError(ex);
                }

                // The engine loop has now fully stopped (awaited above as part of `pending`), so
                // _factoryBatchTask/_removalBatchTask can no longer be reassigned by a new
                // DispatchScaleUp/DispatchScaleDown call - safe to read both here. A Creator or
                // Removal batch still in flight at shutdown must still be waited on. Without this,
                // its telemetry (recorded inside the batch itself) could be recorded after this
                // method has already returned, or its caller's Completion might never resolve.
                var factoryBatchTask = _factoryBatchTask;
                if (factoryBatchTask is not null)
                {
                    try
                    {
                        await factoryBatchTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                    }
                }
                var removalBatchTask = _removalBatchTask;
                if (removalBatchTask is not null)
                {
                    try
                    {
                        await removalBatchTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                    }
                }

                // _heartbeatTask has already completed (awaited above), so RunHeartbeatAsync's own
                // loop can no longer add to this list - safe to snapshot now. Without this wait,
                // DisposeAsync could return while a resource an orphaned heartbeat callback still
                // held hadn't actually been disposed yet - a real leak if the process exits shortly
                // after, not merely a delay.
                List<Task> deferredDisposals;
                lock (_pendingHeartbeatDisposalsGate)
                {
                    deferredDisposals = _pendingHeartbeatDisposals.ToList();
                }
                if (deferredDisposals.Count > 0)
                {
                    try
                    {
                        // Bounded, not indefinite: the orphaned callback that owns these can never
                        // be forcibly cancelled, so it may still be permanently hung. PulseHeartBeat
                        // is reused as the grace period - the same budget the pump gives a single
                        // heartbeat cycle.
                        await Task.WhenAll(deferredDisposals).WaitAsync(PulseHeartBeat).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Warning, not Debug: this is not an ordinary shutdown-vs-failure case like
                        // the sibling messages elsewhere - a resource is now leaked for an
                        // indeterminate time past this DisposeAsync() call, comparably significant
                        // to the existing "RingBuffer without resource" LogWarning.
                        // Worded to cover both entries whose callback is still orphaned and entries
                        // whose callback already returned a verdict with only the item's own
                        // Dispose() still running - _pendingHeartbeatDisposals can hold either.
                        LogWarning($"DisposeAsync did not wait for {deferredDisposals.Count} pending heartbeat item dispose(s) still running past the grace period - their resource(s) will be disposed once/if they finish, but not before this DisposeAsync() call returned.");
                    }
                    catch (Exception ex)
                    {
                        // Each deferred continuation already logs its own DisposeItemAsync failure
                        // internally and never rethrows - this only catches the wrapper itself.
                        LogError(ex);
                    }
                }
            }
            finally
            {
                // Cleanup below must run unconditionally - even if something above this point
                // unexpectedly throws - so pooled items and the buffer's own instrumentation are
                // never left undisposed.
                _availableItems.Writer.TryComplete();
                var remainingItems = new List<T>();
                while (_availableItems.Reader.TryRead(out var item))
                {
                    remainingItems.Add(item);
                }
                // Defensive, per-item: a raw loop calling DisposeItemAsync directly would abort on
                // the first item whose Dispose() throws, leaking every remaining item plus
                // _lifetime/_meter/_activitySource below - permanently, since _disposeGuard makes a
                // second DisposeAsync() call a silent no-op. DisposeItemsDefensivelyAsync exists
                // (also used by DispatchScaleDown's Removal batch) so no caller is exposed to that
                // failure mode.
                await DisposeItemsDefensivelyAsync(remainingItems).ConfigureAwait(false);

                _lifetime.Dispose();
                _meter.Dispose();
                _activitySource.Dispose();
            }
        }

        private Task EnsureWarmupAsync() => Volatile.Read(ref _warmup).Value;

        private async Task WarmupCoreAsync()
        {
            LogMessage("Starting warmup process.");

            bool reached;
            try
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Warmup(completion), LifetimeToken()).ConfigureAwait(false);
                // Bounded by _lifetime.Token: if this command loses its race against disposal and is
                // abandoned unread in the channel, this must not hang forever waiting for a completion
                // signal nobody will ever send.
                reached = await completion.Task.WaitAsync(LifetimeToken()).ConfigureAwait(false);
            }
            // Only an ordinary shutdown of this buffer's own lifetime resolves to reached=false
            // here. A raw OperationCanceledException/TaskCanceledException thrown by the factory
            // itself (e.g. an HttpClient/gRPC/DB-driver's own unrelated internal timeout) must not
            // be swallowed into the generic "did not reach initial capacity" message below,
            // discarding the real exception - it falls through this guard and propagates as-is,
            // the same as any other genuine factory exception on a zero-progress batch.
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                reached = false;
            }

            if (!reached)
            {
                var err = new InvalidOperationException("RingBuffer did not reach initial capacity");
                // An ordinary DisposeAsync() racing this warmup is not a genuine capacity failure,
                // but it surfaces as the same "reached = false": MoveToCapacityAsync's own
                // CreateItemsAsync call absorbs the cancellation internally instead of throwing it,
                // and just returns a partial/zero count, so ProcessCommandAsync's Warmup case
                // resolves the command with TrySetResult(false) directly, not an exception. Logging
                // this as an ERROR would mislead an on-call engineer into thinking the factory was
                // unhealthy during what is actually a clean shutdown.
                if (_lifetime.IsCancellationRequested)
                {
                    LogMessage("Warmup cancelled by shutdown before reaching initial capacity.");
                }
                else
                {
                    LogError(err);
                }
                throw err;
            }

            LogMessage($"End warmup process with {CurrentCapacity} buffers.");

            if (!_disposed && BufferHeartBeat is not null)
            {
                _heartbeatTask = Task.Run(RunHeartbeatAsync);
            }
            if (!_disposed && Elastic)
            {
                _sampleTickTask = Task.Run(RunSampleTickAsync);
            }
        }

        private async ValueTask TurnbackAsync(RingBufferValue<T> value)
        {
            if (!value.Successful) return;
            try
            {
                if (!value.SkipTurnback)
                {
                    await _availableItems.Writer.WriteAsync(value.Current, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    // Enqueues the replacement BEFORE awaiting the old item's own
                    // Dispose()/DisposeAsync(), not after in a `finally`. A `finally` there would
                    // make this ordering depend on a Dispose() that could hang forever - an
                    // unfinished await never lets a `finally` run, so the slot would never be
                    // replaced and CurrentCapacity would stay wrong forever. Pool-wide capacity
                    // truthfulness must not depend on how long, or whether, a caller-owned item's
                    // Dispose() ever returns: if that call hangs, it's this caller's own
                    // DisposeAsync() call hanging too (a local problem for them), not a reason for
                    // shared pool state to go wrong for everyone else. Any exception from
                    // DisposeItemAsync below still propagates to the caller unchanged either way.
                    _commands.Writer.TryWrite(EngineCommand.ReplaceOne());
                    await DisposeItemAsync(value.Current).ConfigureAwait(false);
                }
            }
            catch (ChannelClosedException)
            {
                // The manager was disposed concurrently with the turnback; the item can no longer
                // be returned to the pool, so dispose it instead of leaking it.
                await DisposeItemAsync(value.Current).ConfigureAwait(false);
            }
        }

        #region engine loop (single consumer of _commands; sole owner of _currentCapacity)

        private async Task RunEngineAsync()
        {
            try
            {
                await foreach (var cmd in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await ProcessCommandAsync(cmd).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
        }

        private async Task ProcessCommandAsync(EngineCommand cmd)
        {
            switch (cmd.Kind)
            {
                case EngineCommandKind.Warmup:
                    try
                    {
                        var reached = await MoveToCapacityAsync(Capacity, hasTimeout: false, _lifetime.Token).ConfigureAwait(false);
                        cmd.Completion?.TrySetResult(reached);
                    }
                    catch (Exception ex)
                    {
                        // Surface the real factory failure to WarmupAsync's caller instead of the
                        // generic "did not reach initial capacity" - and, critically, resolve the
                        // TaskCompletionSource so the caller does not hang forever.
                        cmd.Completion?.TrySetException(ex);
                    }
                    break;

                case EngineCommandKind.Switch:
                    var target = ResolveTarget(cmd.Target!.Value);
                    // _scaling here means "a Creator-or-Removal batch dispatched by a previous
                    // Switch, backlog-reactive, floor-guard, or Monitor evaluation is still in
                    // flight" (ADR001V03). That batch now runs in the background instead of
                    // blocking this loop, so a second overlapping dispatch could read the same
                    // stale CurrentCapacity and mutate it on top of that stale read, over- or
                    // under-shooting [MinCapacity, MaxCapacity]. Rejecting here keeps "at most one
                    // batch (create or remove) at a time" and preserves the existing at-most-one-
                    // winner contract for concurrent callers requesting the same target. One
                    // caller-visible consequence, now that Removal's own disposal can take real
                    // time (already true for Creator's factory calls): a second Switch arriving
                    // while a scale-down's disposal is still in flight is rejected for as long as
                    // that disposal takes (bounded by PulseHeartBeat), not just for the near-instant
                    // dequeue that used to be the entire scale-down operation.
                    if (target == CurrentCapacity || _scaling)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                        break;
                    }
                    cmd.Accepted?.TrySetResult(true);
                    // Manual pin (ADR007V03): only set once a real scale is actually dispatched, not
                    // on the already-at-target rejection above - a pin exists to hold a capacity
                    // against the Monitor, which is moot if this call changed nothing. Set before
                    // dispatching, not after, so the pin covers the batch's in-flight time too, not
                    // just the moment after it resolves.
                    _pinExpiresAt = DateTime.UtcNow + cmd.PinDuration!.Value;
                    if (target > CurrentCapacity)
                    {
                        DispatchScaleUp(target, scaleTrigger: "manual", cmd.Completion);
                    }
                    else
                    {
                        DispatchScaleDown(target, scaleTrigger: "manual", cmd.Completion);
                    }
                    break;

                case EngineCommandKind.ReplaceOne:
                    await CreateSingleReplacementAsync().ConfigureAwait(false);
                    // Floor guard (ADR001V03): this is the one live gap it closes - a failed
                    // replacement's own finally block (CreateSingleReplacementAsync) can shrink
                    // CurrentCapacity below MinCapacity with nothing that currently retries it.
                    EvaluateFloorGuard();
                    break;

                case EngineCommandKind.Backlog:
                    EvaluateBacklogReactive();
                    break;

                case EngineCommandKind.Tick:
                    if (_scaling)
                    {
                        // Defensive re-check: RunSampleTickAsync's own check happens at write-time,
                        // before this command was even enqueued, and a narrow window between that
                        // check and this command being dequeued could otherwise let the Monitor
                        // scale here while another batch is still in flight (see the Switch case
                        // above for why that risks an overshoot). This also skips ProcessTick's
                        // _samples.Add for this tick, not just the scale decision - but that has no
                        // observable effect, since FactoryBatchCompleted/RemovalBatchCompleted
                        // unconditionally clear _samples once the in-flight batch finishes (same as
                        // MoveToCapacityAsync's own finally block). Any sample collected during the
                        // batch's whole in-flight window would be discarded anyway.
                        break;
                    }
                    if (_pinExpiresAt is { } pinExpiresAt)
                    {
                        // Manual pin (ADR007V03): substitutes for the Monitor's own predictive
                        // output for the pin's duration, so Tick is skipped here too, for the same
                        // reason as while _scaling is true (see above) - any sample collected during
                        // the pin would only feed a decision that must not run yet. The floor guard
                        // and backlog-reactive signal are never gated by this: they run from their
                        // own independent triggers (ReplaceOne, Backlog, FactoryBatchCompleted,
                        // RemovalBatchCompleted), never from Tick, so this check can't affect them.
                        if (DateTime.UtcNow < pinExpiresAt)
                        {
                            break;
                        }
                        _pinExpiresAt = null;
                    }
                    ProcessTick();
                    break;

                case EngineCommandKind.FactoryBatchCompleted:
                    // Telemetry (activity/meter) was already finalized unconditionally inside
                    // DispatchScaleUp's own background task - see its comment for why. This case
                    // only applies the sole-owner state changes: capacity, _scaling, and the
                    // caller's completion.
                    //
                    // Clear in-flight status BEFORE resolving the caller's completion: a sequential
                    // caller that awaits completion (LockWhenScaling) must never observe _scaling
                    // still true once its own await returns, or its very next SwitchToAsync could
                    // be spuriously rejected by the check above.
                    _scaling = false;
                    if (cmd.Created > 0)
                    {
                        // Applied against the live CurrentCapacity at completion time, not the value
                        // captured at dispatch. With batches serialized (one in flight at a time)
                        // nothing else can have moved capacity in between, but writing it this way
                        // keeps the arithmetic correct even if a future change ever allows otherwise.
                        Volatile.Write(ref _currentCapacity, CurrentCapacity + cmd.Created);
                    }
                    var scaledUp = cmd.Created == cmd.Quantity;
                    _samples.Clear();
                    if (cmd.Completion is not null)
                    {
                        if (cmd.Failure is not null)
                        {
                            cmd.Completion.TrySetException(cmd.Failure);
                        }
                        else
                        {
                            cmd.Completion.TrySetResult(scaledUp);
                        }
                    }
                    else if (cmd.Failure is not null)
                    {
                        // No caller is waiting on a Backlog-triggered scale-up; logging is the only
                        // outcome needed, same as before this batch was dispatched in the background.
                        LogError(cmd.Failure);
                    }
                    // Floor guard runs first (ADR001V03's signal priority: floor guard >
                    // backlog-reactive > manual pin > Monitor). A batch that only partially
                    // created its target (or a floor-guard batch that failed outright) can still
                    // leave CurrentCapacity below MinCapacity, and since only one batch may be in
                    // flight at a time, this is also where a still-unresolved breach gets its next
                    // retry. Dispatching here sets _scaling back to true, making the
                    // EvaluateBacklogReactive call below a no-op for this round - the floor
                    // legitimately wins the single in-flight-batch slot over ordinary backlog
                    // demand, not an oversight.
                    EvaluateFloorGuard();
                    // Re-evaluate backlog right away, regardless of what triggered this batch: any
                    // callers that started waiting while this one was in flight had their own
                    // Backlog command skipped (only one batch may be in flight at a time), and must
                    // not have to wait for their own AcquireTimeout to get addressed - see
                    // EvaluateBacklogReactive's own remarks for why this deferred re-check is
                    // equivalent to netting out in-flight-creating under that one-at-a-time rule.
                    EvaluateBacklogReactive();
                    break;

                case EngineCommandKind.RemovalBatchCompleted:
                    // Telemetry (activity/meter) was already finalized unconditionally inside
                    // DispatchScaleDown's own background task - see its comment for why. This case
                    // only applies the sole-owner state changes: capacity, _scaling, and the
                    // caller's completion. Same ordering as FactoryBatchCompleted: _scaling clears
                    // BEFORE the caller's completion resolves, so a sequential caller
                    // (LockWhenScaling) never observes it still true once its own await returns.
                    _scaling = false;
                    if (cmd.Removed > 0)
                    {
                        // Reflects confirmed completion, not the request (ADR001V03): the items
                        // were already gone from _availableItems the instant DispatchScaleDown
                        // dequeued them, but CurrentCapacity only reflects that once Removal's own
                        // disposal has actually finished - the same "only the sole owner mutates,
                        // only on confirmed completion" rule Creator's capacity increment follows.
                        Volatile.Write(ref _currentCapacity, CurrentCapacity - cmd.Removed);
                    }
                    var scaledDown = cmd.Removed == cmd.Quantity;
                    _samples.Clear();
                    // No Failure/exception path here, unlike FactoryBatchCompleted: a scale-down
                    // can only fully or partially succeed (see DispatchScaleDown's own comment),
                    // never genuinely fail.
                    cmd.Completion?.TrySetResult(scaledDown);
                    // Same floor-guard-first-then-backlog re-check as FactoryBatchCompleted, for
                    // the same reason: either signal may have been skipped (both check _scaling
                    // before dispatching) while this removal's disposal was in flight.
                    EvaluateFloorGuard();
                    EvaluateBacklogReactive();
                    break;
            }
        }

        // Floor guard (ADR001V03): the highest-priority signal of all (floor guard >
        // backlog-reactive > manual pin > Monitor). It protects the pool's minimum contractual
        // floor - CurrentCapacity actually dropping below MinCapacity - which today can only
        // happen via CreateSingleReplacementAsync's own finally block (a failed heartbeat- or
        // Invalidate()-triggered replacement; see its own comment). Warmup can't silently leave
        // this gap: WarmupCoreAsync already throws "did not reach initial capacity" on any
        // shortfall, so the caller learns synchronously and no background guard is needed there.
        //
        // Undebounced by design (per the ADR): every evaluation that finds a breach dispatches
        // immediately. The only pacing comes from Creator's own existing consecutive-failure
        // backoff (CreateItemsAsync), not from anything added here. There is no public "genuinely
        // below minimum" property yet - that surface is deferred to ADR007V03, same as
        // AutoScaleAcquireFault's numberOfFaults - so until it exists, an elapsed grace window
        // (FactoryTimeout, reused per the ADR) is reported via LogError, the closest honest
        // substitute available today.
        private void EvaluateFloorGuard()
        {
            var deficit = FloorGuardDecision.EvaluateBreach(CurrentCapacity, MinCapacity);
            if (deficit is null)
            {
                _floorBreachDetectedAt = null;
                _floorBreachLastReportedAt = null;
                return;
            }
            _floorBreachDetectedAt ??= DateTime.UtcNow;
            var now = DateTime.UtcNow;
            if (FloorGuardDecision.ShouldReportNow(_floorBreachDetectedAt.Value, _floorBreachLastReportedAt, now, FactoryTimeout))
            {
                LogError(new InvalidOperationException($"RingBuffer below minimum capacity for longer than one FactoryTimeout cycle: current={CurrentCapacity}, minimum={MinCapacity}."));
                _floorBreachLastReportedAt = now;
            }
            if (_scaling)
            {
                return;
            }
            DispatchScaleUp(MinCapacity, scaleTrigger: "floor", completion: null);
        }

        // Backlog-reactive signal (ADR001V03): reacts to real, currently-waiting callers instead
        // of a coarse fault count, before any AcquireTimeout elapses, proportional to the actual
        // unmet demand - waiting callers minus what can already serve them (idle items). It
        // replaces the old fault-count-based trigger entirely: this signal is simply always
        // active for an elastic pool, gated only by Elastic below.
        //
        // The ADR's formula also nets out "in-flight-creating" so overlapping backlog waves never
        // duplicate a request. Here that term is always zero by construction: a batch already in
        // flight (from this signal or Switch) means _scaling is true, so this method returns
        // before computing a gap at all, deferring to the follow-up call its own caller makes once
        // FactoryBatchCompleted clears _scaling. Any residual backlog gets addressed then, without
        // ever risking two overlapping batches independently reading a stale CurrentCapacity - the
        // same overshoot risk the Switch case's one-at-a-time rule protects against.
        private void EvaluateBacklogReactive()
        {
            if (!Elastic || _scaling || CurrentCapacity >= MaxCapacity)
            {
                return;
            }
            // A known, accepted approximation ("simple, not exact", same spirit as the
            // consecutive-failures counter under concurrency, ADR001V03). _waitingCount is
            // decremented in the served caller's own continuation (AcquireCoreAsync, right after
            // ReadAsync returns - deliberately not in that method's shared finally, to keep this
            // window as narrow as possible), which still runs asynchronously with respect to this
            // method's caller. So a follow-up evaluation (from FactoryBatchCompleted, right after a
            // batch completes) can occasionally still see a just-served caller as "waiting" a
            // moment longer than reality, computing a gap that's briefly too high and dispatching
            // one extra small batch before the count catches up. This never risks exceeding
            // MaxCapacity (still capped below), and self-corrects on the next evaluation once the
            // served caller's own decrement has run - at worst, a few more items than strictly
            // necessary get created, which a later scale-down naturally reabsorbs once idle.
            var gap = Volatile.Read(ref _waitingCount) - _availableItems.Reader.Count;
            if (gap <= 0)
            {
                return;
            }
            var target = Math.Min(CurrentCapacity + gap, MaxCapacity);
            if (target == CurrentCapacity)
            {
                return;
            }
            DispatchScaleUp(target, scaleTrigger: "backlog", completion: null);
        }

        // Creator (ADR001V03): dispatches a scale-up's bounded-concurrent factory batch onto the
        // thread pool instead of awaiting it inline, so the engine's single consumer thread stays
        // free to process other commands (a floor-guard replenishment, a heartbeat-driven
        // ReplaceOne, ...) while the batch is in flight. _currentCapacity is still mutated only on
        // the engine thread, later, when the batch's own FactoryBatchCompleted command is
        // processed - preserving the Orchestrator's sole-owner guarantee. Used only by Switch
        // (manual), EvaluateBacklogReactive (backlog), and EvaluateFloorGuard (floor) scale-up
        // requests. Warmup's scale-up still calls MoveToCapacityAsync directly, unchanged (see its
        // own remarks for why), and Tick's scale-down uses DispatchScaleDown below instead, the
        // Removal counterpart.
        private void DispatchScaleUp(int target, string scaleTrigger, TaskCompletionSource<bool>? completion)
        {
            var current = CurrentCapacity;
            var quantity = target - current;
            _scaling = true;

            var activity = _activitySource.StartActivity("RingBufferPlus.Scale");
            activity?.SetTag("buffer.name", Name);
            activity?.SetTag("direction", "up");
            activity?.SetTag("trigger", scaleTrigger);
            activity?.SetTag("target", target);
            var sw = Stopwatch.StartNew();
            LogMessage($"Starting ScaleUp {quantity}. Trigger: {scaleTrigger}. Target: {target}.");

            _factoryBatchTask = Task.Run(async () =>
            {
                int created;
                bool hadGenuineFailure;
                Exception? threw = null;
                try
                {
                    (created, hadGenuineFailure) = await CreateItemsAsync(quantity, hasTimeout: true, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // CreateItemsAsync's own throw path (zero items created, every attempt failed
                    // for a real reason) skips the tuple return entirely - always a genuine failure
                    // at that point, since CreateItemsAsync only ever throws its own lastFailure,
                    // never an ordinary cancellation.
                    created = 0;
                    hadGenuineFailure = true;
                    threw = ex;
                }
                sw.Stop();
                LogMessage($"End ScaleUp. Trigger: {scaleTrigger}. Target: {target}.");

                // Telemetry (activity/meter) is finalized here, unconditionally, rather than
                // deferred to the FactoryBatchCompleted command below: it is independent,
                // thread-safe instrumentation, not sole-owner state, and must still be reported
                // even if the manager is disposed (channel closed, or DisposeAsync simply reaches
                // its own listener-disposal point) before this batch finishes - DisposeAsync's own
                // await on _factoryBatchTask is what bounds that race.
                var scaledUp = created == quantity;
                var cancelledByShutdown = !scaledUp && _lifetime.IsCancellationRequested && !hadGenuineFailure;
                var statusOk = scaledUp || cancelledByShutdown;
                activity?.SetStatus(statusOk ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                // scale.operations/scale.duration both carry "success" - the activity carries it too,
                // for the same metric/trace tag-set parity acquire's own telemetry keeps.
                activity?.SetTag("success", scaledUp);
                activity?.SetTag("cancelled", cancelledByShutdown);
                activity?.Dispose();
                _scaleOperations.Add(1,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("direction", "up"),
                    new KeyValuePair<string, object?>("trigger", scaleTrigger),
                    new KeyValuePair<string, object?>("target", target),
                    new KeyValuePair<string, object?>("success", scaledUp),
                    new KeyValuePair<string, object?>("cancelled", cancelledByShutdown));
                _scaleDuration.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("direction", "up"),
                    new KeyValuePair<string, object?>("trigger", scaleTrigger),
                    new KeyValuePair<string, object?>("target", target),
                    new KeyValuePair<string, object?>("success", scaledUp),
                    new KeyValuePair<string, object?>("cancelled", cancelledByShutdown));

                var posted = _commands.Writer.TryWrite(EngineCommand.FactoryBatchCompleted(quantity, created, threw, completion, scaleTrigger));
                if (!posted)
                {
                    // The manager was disposed while this batch was in flight - the engine loop is
                    // gone, so nothing will ever apply the capacity mutation or clear _scaling.
                    // Resolve the caller (if any) directly instead of leaving it hanging forever;
                    // capacity itself is moot now, since the buffer is shutting down.
                    completion?.TrySetResult(false);
                }
            });
        }

        // Removal (ADR001V03): dispatches a scale-down's disposal of already-idle items onto the
        // thread pool instead of awaiting it inline, so the engine's single consumer thread stays
        // free to process other commands while the batch is in flight - the same isolation
        // DispatchScaleUp already gives Creator, for the same reason (Dispose()/DisposeAsync() on
        // a real connection can block on I/O just as a factory call can). Per the ADR's wording,
        // the Orchestrator dequeues here, on the engine thread, before handing off to the
        // background task - Removal itself never touches the channel. TryRead is fast and
        // non-blocking regardless of how many items are actually idle right now (opportunistic,
        // partial if needed, the same "keep whatever progress was made" spirit already applied to
        // scale-up) - only the disposal that follows can ever block, and that's exactly what gets
        // isolated. _currentCapacity is still mutated only on the engine thread, later, when the
        // batch's own RemovalBatchCompleted command is processed, per the ADR's "confirmed
        // completion, never the request" rule - the same rule Creator's capacity increment
        // follows. Used by Switch (manual) and Tick's Monitor-driven (auto) scale-down requests.
        private void DispatchScaleDown(int target, string scaleTrigger, TaskCompletionSource<bool>? completion)
        {
            var current = CurrentCapacity;
            var quantity = current - target;
            _scaling = true;

            var removed = new List<T>(quantity);
            while (removed.Count < quantity && _availableItems.Reader.TryRead(out var item))
            {
                removed.Add(item);
            }

            var activity = _activitySource.StartActivity("RingBufferPlus.Scale");
            activity?.SetTag("buffer.name", Name);
            activity?.SetTag("direction", "down");
            activity?.SetTag("trigger", scaleTrigger);
            activity?.SetTag("target", target);
            var sw = Stopwatch.StartNew();
            LogMessage($"Starting ScaleDown {quantity}. Trigger: {scaleTrigger}. Target: {target}.");

            _removalBatchTask = Task.Run(async () =>
            {
                if (removed.Count > 0)
                {
                    await DisposeItemsDefensivelyAsync(removed).ConfigureAwait(false);
                }
                sw.Stop();
                LogMessage($"End ScaleDown. Trigger: {scaleTrigger}. Target: {target}.");

                // Telemetry (activity/meter) is finalized here, unconditionally, same reasoning as
                // DispatchScaleUp's own comment: independent, thread-safe instrumentation, not
                // sole-owner state, must still be reported even if the manager is disposed before
                // this batch finishes.
                //
                // Scale-down differs from scale-up in kind, not just degree: the dequeue above
                // never observes any token, and disposal never throws (per-item failures are
                // already swallowed by DisposeItemsDefensivelyAsync). A scale-down can only fully
                // or partially succeed ("not enough idle items were available right now" is a
                // normal, by-design outcome, not a failure) - there's no cancellation path and no
                // genuine-failure path to distinguish here at all. So `!scaledDown` must never be
                // reported as ActivityStatusCode.Error, since that would misrepresent this normal
                // outcome as a fault. "cancelled" is still always emitted (false), to preserve the
                // tag contract every scale operation carries, not just the ones where it can
                // actually be true.
                var scaledDown = removed.Count == quantity;
                // A manual (pinned) scale-down that only partially completes (not enough idle
                // items were available right now) has nothing retrying it while the pin is active.
                // The Monitor is the only signal that would otherwise finish the reduction as
                // retained items become idle again, and it stays suppressed for the whole pin
                // duration. This is accepted, not fixed: the pool is never corrupted
                // (CurrentCapacity stays truthful, invariants hold), and it self-corrects once the
                // pin expires and ordinary Monitor ticks resume - but it's silent until then, so
                // this logs it explicitly. Not relevant for an "auto"-triggered (Monitor-driven)
                // scale-down: that path re-evaluates on every subsequent tick on its own, with no
                // pin ever suppressing it.
                if (!scaledDown && scaleTrigger == "manual")
                {
                    LogWarning($"ScaleDown to {target} only partially completed ({removed.Count}/{quantity} items removed) while a manual pin is active - the remaining reduction will not be retried until the pin expires.");
                }
                activity?.SetStatus(ActivityStatusCode.Ok);
                // Same tag-set parity as DispatchScaleUp above - scale.operations/scale.duration
                // carry "success", so the activity does too.
                activity?.SetTag("success", scaledDown);
                activity?.SetTag("cancelled", false);
                activity?.Dispose();
                _scaleOperations.Add(1,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("direction", "down"),
                    new KeyValuePair<string, object?>("trigger", scaleTrigger),
                    new KeyValuePair<string, object?>("target", target),
                    new KeyValuePair<string, object?>("success", scaledDown),
                    new KeyValuePair<string, object?>("cancelled", false));
                _scaleDuration.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("direction", "down"),
                    new KeyValuePair<string, object?>("trigger", scaleTrigger),
                    new KeyValuePair<string, object?>("target", target),
                    new KeyValuePair<string, object?>("success", scaledDown),
                    new KeyValuePair<string, object?>("cancelled", false));

                var posted = _commands.Writer.TryWrite(EngineCommand.RemovalBatchCompleted(quantity, removed.Count, completion));
                if (!posted)
                {
                    // The manager was disposed while this batch was in flight - the engine loop is
                    // gone, so nothing will ever apply the capacity mutation or clear _scaling.
                    // Resolve the caller (if any) directly instead of leaving it hanging forever;
                    // capacity itself is moot now, since the buffer is shutting down.
                    completion?.TrySetResult(scaledDown);
                }
            });
        }

        // Monitor (ADR001V03's lowest-priority signal; ADR003V03's algorithm): a sliding-window
        // percentile + safety buffer "fair level", adjusted by a linear-regression trend projected
        // a configurable horizon ahead, clamped to [MinCapacity, MaxCapacity].
        //
        // Demand = (in-use items) + (currently-waiting callers), i.e. CurrentCapacity minus idle,
        // plus _waitingCount - never idle alone, which is clamped at zero and so blind to unmet
        // demand (see AutoScaleMonitor's own remarks on why an idle-derived proxy would silently
        // flatten the regression trend under sustained saturation).
        //
        // While demand keeps pace with or exceeds capacity ("active"), the window is paused
        // entirely and cleared the instant that episode ends - see _monitorActive's own remarks
        // for exactly which states satisfy this (broader than just genuine backlog: ordinary full
        // utilization with nobody waiting qualifies too). A genuinely steady period (no scale op
        // at all, deadband absorbing every small drift) still lets the window grow to SamplesCount
        // before demand next moves - a real, accepted trade-off of the shipped defaults, not
        // something this field or the per-scale-op clears (MoveToCapacityAsync/
        // FactoryBatchCompleted/RemovalBatchCompleted) prevent.
        //
        // Otherwise, this tick's demand joins the sliding window (bounded to SamplesCount,
        // evaluated every tick once at least 2 samples exist) and the resulting target is compared
        // against CurrentCapacity: a change smaller than MonitorDeadband is ignored (measured to
        // cut oscillation under flat-but-noisy demand from 48 reversals in 300 ticks down to 1); a
        // larger increase or decrease dispatches a background batch (DispatchScaleUp/
        // DispatchScaleDown, same as backlog/floor/manual). This method has no async work of its
        // own left, now that Removal moved scale-down's execution off the engine thread too.
        private void ProcessTick()
        {
            var idle = _availableItems.Reader.Count;
            var demand = CurrentCapacity - idle + Volatile.Read(ref _waitingCount);
            var active = demand >= CurrentCapacity;
            if (_monitorActive && !active)
            {
                _samples.Clear();
            }
            _monitorActive = active;
            if (active)
            {
                return;
            }

            _samples.Add(demand);
            if (_samples.Count > SamplesCount)
            {
                _samples.RemoveAt(0);
            }
            if (_samples.Count < 2)
            {
                return;
            }

            var target = AutoScaleMonitor.EvaluateTarget(_samples, MonitorPercentileP, MonitorSafetyBuffer, MonitorHorizon, MinCapacity, MaxCapacity);
            if (target == CurrentCapacity)
            {
                // Without this, the Monitor's tick decision would leave no trace at all when it
                // decides NOT to scale, since scale.* telemetry only exists for a dispatched
                // operation. LogMessage gates on IsEnabled(Debug), so this costs nothing when the
                // caller hasn't opted into Debug-level logs, even though it runs roughly every tick
                // for the buffer's whole lifetime, not just per scale operation.
                LogMessage($"Monitor tick: demand={demand}, target={target} equals current capacity {CurrentCapacity} - no scale.");
                return;
            }
            // Reachability cap: MonitorDeadband must never exceed the maximum delta actually
            // reachable in the direction target is asking for. Otherwise a small
            // Capacity/MinCapacity/MaxCapacity span (e.g. ElasticCapacity(4, 2, 8), span 2 < the
            // default deadband of 3) would make that boundary mathematically unreachable through
            // this gate forever, however idle or saturated the buffer becomes.
            var maxReachableDelta = target > CurrentCapacity ? MaxCapacity - CurrentCapacity : CurrentCapacity - MinCapacity;
            var effectiveDeadband = Math.Min(MonitorDeadband, maxReachableDelta);
            if (Math.Abs(target - CurrentCapacity) < effectiveDeadband)
            {
                LogMessage($"Monitor tick: demand={demand}, target={target}, current={CurrentCapacity} - within deadband ({effectiveDeadband}), no scale.");
                return;
            }
            LogMessage($"Monitor tick: demand={demand}, target={target}, current={CurrentCapacity} - dispatching {(target > CurrentCapacity ? "scale-up" : "scale-down")}.");
            if (target > CurrentCapacity)
            {
                DispatchScaleUp(target, scaleTrigger: "auto", completion: null);
            }
            else
            {
                DispatchScaleDown(target, scaleTrigger: "auto", completion: null);
            }
        }

        private int ResolveTarget(ScaleSwitch value) => value switch
        {
            ScaleSwitch.MinCapacity => MinCapacity,
            ScaleSwitch.MaxCapacity => MaxCapacity,
            _ => Capacity
        };

        // Warmup is now this method's only caller (Removal, ADR001V03), since Switch's and
        // Tick's scale-down paths moved to DispatchScaleDown, and their scale-up paths moved to
        // DispatchScaleUp. So this is always a scale-up (CurrentCapacity starts at 0, Capacity is
        // always >= 2), always with no
        // scaleTrigger (the initial fill is not a "scale operation" the scale.* metrics describe,
        // see the class remarks). The scale-down branch, the telemetry block, and the
        // genuine-failure bookkeeping that only ever fed that now-unreachable telemetry were
        // removed along with it, not just simplified - nothing can reach them anymore.
        private async Task<bool> MoveToCapacityAsync(int target, bool hasTimeout, CancellationToken token)
        {
            var current = CurrentCapacity;
            if (target == current) return true;

            _scaling = true;
            try
            {
                var quantity = target - current;
                LogMessage($"Starting ScaleUp {quantity}.");
                var (created, _) = await CreateItemsAsync(quantity, hasTimeout, token).ConfigureAwait(false);
                LogMessage("End ScaleUp.");
                var ok = created == quantity;
                // A partial scale-up still gained real, usable capacity - advance by however many
                // items were actually created, not just on hitting the full target.
                if (created > 0)
                {
                    Volatile.Write(ref _currentCapacity, current + created);
                }
                return ok;
            }
            finally
            {
                _scaling = false;
                // Discard whatever samples (if any) accumulated before/during this scale -
                // they describe the pre-scale capacity, not the one the buffer has now. The next
                // scale-down decision must be based on a fresh window sampled after this point.
                _samples.Clear();
            }
        }

        // Creator (ADR001V03): waits out the current backoff window (if any) before a factory
        // attempt starts. A zero streak (the common, healthy case) returns immediately - no delay,
        // no allocation. Exponential, capped at FactoryBackoffMax so a long-broken factory does not
        // grow the wait unboundedly: 1st failure -> FactoryBackoffBase, 2nd -> x2, 3rd -> x4, ...
        private async Task ApplyFactoryBackoffAsync(CancellationToken token)
        {
            var streak = Volatile.Read(ref _factoryFailureStreak);
            if (streak <= 0) return;

            var multiplier = Math.Pow(2, streak - 1);
            var ticks = Math.Min(FactoryBackoffBase.Ticks * multiplier, FactoryBackoffMax.Ticks);
            await Task.Delay(TimeSpan.FromTicks((long)ticks), token).ConfigureAwait(false);
        }

        private async Task<(int Created, bool HadGenuineFailure)> CreateItemsAsync(int quantity, bool hasTimeout, CancellationToken token)
        {
            // Creator (ADR001V03): bounded concurrent creation, not one attempt at a time - up to
            // MaxConcurrentFactoryCalls Factory calls can be in flight simultaneously, mitigating a
            // large batch (warmup, scale-up, floor-guard replenishment) flooding a
            // struggling-but-technically-accepting downstream with simultaneous connection attempts.
            var created = new ConcurrentBag<T>();
            var stateLock = new object();
            Exception? lastFailure = null;
            var consecutiveFailures = 0;
            var giveUp = false;

            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            // The deadline scales with the work actually requested (each item already has its own
            // FactoryTimeout-bounded attempt), not with the sampling cadence (SamplesBase). A
            // fixed, sampling-derived deadline could end up smaller than quantity * FactoryTimeout
            // for some delta/FactoryTimeout combinations, making a routine scale-up structurally
            // impossible regardless of the factory's actual health. It's deliberately not
            // tightened to account for MaxConcurrentFactoryCalls: a looser bound that scales with
            // the sequential worst case is always safe (never fires prematurely) - it doesn't need
            // to be the tightest possible one.
            //
            // Armed lazily, once, by whichever attempt clears backoff first, rather than up front:
            // `overall`'s wall-clock timer runs regardless of what token a wait is bound to, so
            // arming it up front would let time spent waiting out an elevated backoff streak
            // (ApplyFactoryBackoffAsync below, deliberately not bound to `overall`) eat into this
            // deadline before Factory is ever called - the same self-inflicted failure the
            // paragraph above already guards against, just via the wall clock instead of the token.
            var deadlineArmed = 0;
            void ArmDeadline()
            {
                if (hasTimeout && Interlocked.CompareExchange(ref deadlineArmed, 1, 0) == 0)
                {
                    overall.CancelAfter(TimeSpan.FromTicks(FactoryTimeout.Ticks * quantity));
                }
            }
            using var gate = new SemaphoreSlim(MaxConcurrentFactoryCalls, MaxConcurrentFactoryCalls);

            // One attempt per requested item - a single item's timeout/exception no longer
            // abandons the whole batch by default. MaxConsecutiveFactoryFailures (default 0) still
            // gives up on the remaining not-yet-started items once a real streak of failures
            // happens, resetting on any success. Under real concurrency, "consecutive" no longer
            // has an exact, ordered meaning, since this is a single shared counter, not one per
            // lane - the same "simple, not a circuit-breaker" approximation the ADR calls for, and
            // identical to the original sequential behavior whenever MaxConcurrentFactoryCalls is 1.
            //
            // A known, deliberately kept trade-off between these two defaults: all `quantity`
            // attempts below are launched at once, so the first MaxConcurrentFactoryCalls (default
            // 4) of them always acquire a gate slot and run to real completion before any of them
            // can report a failure and set giveUp. A fully broken factory therefore still gets up
            // to 4 genuine attempts per batch, not 1, despite MaxConsecutiveFactoryFailures's
            // default of 0 ("give up on the first failure") - already covered by this method's own
            // XML doc on maxConsecutiveFactoryFailures. Two alternatives were considered and
            // rejected: lowering MaxConcurrentFactoryCalls's default to 1 would restore an exact
            // "N consecutive" guarantee, but defeats the whole reason Creator is concurrent by
            // default (it would serialize every ordinary healthy-factory batch, not just the
            // pathological broken-factory case); raising MaxConsecutiveFactoryFailures's default
            // would make the number honest but moves the wrong direction, since more tolerance
            // means more wasted attempts against a broken factory, not fewer. Kept as-is: the real
            // cost is bounded (wall-clock stays ~1x FactoryTimeout regardless, since the wave is
            // concurrent, not sequential) and no default change fixes it without a worse trade-off
            // elsewhere.
            async Task AttemptAsync()
            {
                lock (stateLock) { if (giveUp) return; }
                // Backoff happens before acquiring a concurrency slot, not while holding one - a
                // backed-off attempt shouldn't tie up a permit that other, not-yet-throttled
                // attempts could use instead. Bounded by `token` (shutdown), deliberately not
                // `overall` (the batch's quantity * FactoryTimeout deadline): a long-elevated
                // streak (backoff capped at FactoryBackoffMax, e.g. 5s) could otherwise consume the
                // whole deadline before Factory is ever called - the same "routine scale-up
                // structurally impossible" failure the deadline formula above exists to prevent. A
                // healthy-but-currently-backed-off factory must always get a real attempt, not
                // silently never reach one.
                await ApplyFactoryBackoffAsync(token).ConfigureAwait(false);
                ArmDeadline();
                lock (stateLock) { if (giveUp) return; }
                await gate.WaitAsync(overall.Token).ConfigureAwait(false);
                try
                {
                    lock (stateLock) { if (giveUp) return; }
                    using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    factoryTimeout.CancelAfter(FactoryTimeout);
                    try
                    {
                        // factoryTimeout is linked from overall, so it fires on everything overall
                        // does, plus the per-item deadline. Passing it here loses no cancellation
                        // signal, but actually stops a well-behaved factory once .WaitAsync gives
                        // up on it, instead of leaving it running orphaned.
                        var item = await Factory(factoryTimeout.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                        created.Add(item);
                        lock (stateLock) { consecutiveFailures = 0; }
                        Interlocked.Exchange(ref _factoryFailureStreak, 0);
                    }
                    catch (OperationCanceledException) when (factoryTimeout.IsCancellationRequested && !overall.IsCancellationRequested)
                    {
                        var timeout = new TimeoutException("Timeout factory");
                        LogError(timeout);
                        lock (stateLock)
                        {
                            lastFailure = timeout;
                            if (++consecutiveFailures > MaxConsecutiveFactoryFailures) giveUp = true;
                        }
                        Interlocked.Increment(ref _factoryFailureStreak);
                    }
                    catch (Exception ex) when (!overall.IsCancellationRequested)
                    {
                        LogError(ex);
                        lock (stateLock)
                        {
                            lastFailure = ex;
                            if (++consecutiveFailures > MaxConsecutiveFactoryFailures) giveUp = true;
                        }
                        Interlocked.Increment(ref _factoryFailureStreak);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            try
            {
                var attempts = new Task[quantity];
                for (var i = 0; i < quantity; i++)
                {
                    attempts[i] = AttemptAsync();
                }
                await Task.WhenAll(attempts).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The overall deadline firing before every item could be attempted isn't the only
                // way to get here: a normal DisposeAsync racing this call also cancels the same
                // linked token. Only log a timeout when the deadline itself actually elapsed - a
                // caller-token/lifetime cancellation is an ordinary shutdown, not evidence the
                // factory is unhealthy.
                if (token.IsCancellationRequested)
                {
                    LogMessage($"ScaleUp cancelled by shutdown, {created.Count}/{quantity} item(s) already created.");
                }
                else
                {
                    LogError(new TimeoutException($"Timeout ScaleUp {created.Count}/{quantity} - keeping the {created.Count} item(s) already created."));
                }
            }

            foreach (var item in created)
            {
                await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            if (created.IsEmpty && lastFailure is not null)
            {
                // Nothing at all was gained and every attempt failed for a real reason (not
                // just running out of the overall deadline) - surface that real failure
                // instead of silently reporting zero progress (same contract as before).
                throw lastFailure;
            }
            return (created.Count, lastFailure is not null);
        }

        private async Task DisposeItemsDefensivelyAsync(IEnumerable<T> items)
        {
            // A pooled item's own Dispose()/DisposeAsync() has no bound anywhere else, unlike
            // Factory (FactoryTimeout) and the heartbeat callback (PulseHeartBeat). Both callers of
            // this method depend on it never hanging: DisposeAsync()'s drain loop (a hang there
            // delays/blocks shutdown itself), and a scale-down's removal, which once ran inline on
            // the single-consumer engine thread, where a hang stalled every other command until
            // this bound gave up on it. Removal (ADR001V03) has since moved that call onto
            // DispatchScaleDown's own background task, so this bound no longer protects the engine
            // thread for that caller - it now only bounds how long the background batch itself
            // waits before giving up on a still-hanging item. PulseHeartBeat is reused as the grace
            // period here too, the same "can't cancel external code, so stop waiting instead"
            // approach already used for the heartbeat case.
            //
            // Two more things this method handles: a plain synchronous IDisposable.Dispose()
            // blocks inline before any await point exists for WaitAsync to bound, so Task.Run below
            // pushes it onto a thread-pool thread first (same as the existing
            // Task.Run(() => BufferHeartBeat?.Invoke(...)) pattern), so the grace period actually
            // applies to it too. And a plain sequential foreach would cost N x PulseHeartBeat for N
            // hung items - Task.WhenAll bounds the whole batch by roughly one PulseHeartBeat instead.
            var disposals = new List<Task>();
            foreach (var item in items)
            {
                disposals.Add(DisposeOneItemDefensivelyAsync(item));
            }
            await Task.WhenAll(disposals).ConfigureAwait(false);
        }

        private async Task DisposeOneItemDefensivelyAsync(T item)
        {
            // DisposeItemAsync(item) never actually gets cancelled by the timeout - there's no way
            // to force that on arbitrary user code. It keeps running in the background if the wait
            // below times out, but it can never fault an unobserved exception either: any
            // exception it eventually throws is still caught below, just later than this method
            // waited for.
            var disposeTask = Task.Run(() => DisposeItemAsync(item).AsTask());
            try
            {
                await disposeTask.WaitAsync(PulseHeartBeat).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogWarning("A pooled item's Dispose()/DisposeAsync() did not complete within the grace period - it will keep running in the background, but this call is no longer waiting for it.");
                _ = disposeTask.ContinueWith(t =>
                {
                    if (t.IsFaulted) LogError(t.Exception!.GetBaseException());
                }, TaskScheduler.Default);
            }
            catch (Exception disposeEx)
            {
                // One item's Dispose()/DisposeAsync() throwing must not stop the rest from being
                // disposed, nor escape and kill the engine loop.
                LogError(disposeEx);
            }
        }

        private async Task CreateSingleReplacementAsync()
        {
            // Creator (ADR001V03): same shared, cross-call backoff as CreateItemsAsync - a factory
            // that keeps failing every time it's asked for a replacement gets throttled, not
            // hammered on every single Invalidate()/heartbeat-triggered cycle. This currently runs
            // inline on the engine thread (ProcessCommandAsync's ReplaceOne case), so a long
            // backoff here also delays every other command behind it in the queue, until Creator's
            // execution is decoupled from it. Accepted for now, since the alternative (no backoff
            // at all) is exactly the hammering this mechanism exists to prevent - decoupling is
            // what would actually resolve the tension.
            await ApplyFactoryBackoffAsync(_lifetime.Token).ConfigureAwait(false);

            using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            factoryTimeout.CancelAfter(FactoryTimeout);
            var replaced = false;
            try
            {
                // factoryTimeout is linked from _lifetime, so it fires on everything _lifetime
                // does, plus the per-item deadline. Passing it here loses no cancellation signal,
                // but actually stops a well-behaved factory once .WaitAsync gives up on it,
                // instead of leaving it running orphaned.
                var item = await Factory(factoryTimeout.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                replaced = true;
                Interlocked.Exchange(ref _factoryFailureStreak, 0);
            }
            // Same distinction as CreateItemsAsync: a normal DisposeAsync racing this
            // replacement cancels the same _lifetime token the per-item timeout is linked from -
            // that is an ordinary shutdown, not evidence the factory is unhealthy.
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                LogMessage("Replacement cancelled by shutdown.");
            }
            // Only the per-item factoryTimeout's own deadline actually elapsing counts as a
            // genuine timeout worth fabricating a TimeoutException for. A raw
            // OperationCanceledException/TaskCanceledException thrown by the factory itself (its
            // own unrelated internal timeout, e.g. HttpClient/gRPC/a DB driver) is not that, and
            // logging a fabricated TimeoutException for it would discard the real exception.
            catch (OperationCanceledException) when (factoryTimeout.IsCancellationRequested)
            {
                LogError(new TimeoutException("Timeout factory (replacement)"));
                Interlocked.Increment(ref _factoryFailureStreak);
            }
            catch (Exception ex)
            {
                // A non-cancellation factory failure must not escape and kill the engine loop; no
                // caller is waiting on a replacement, so logging is the only outcome needed here.
                // This also catches a factory-thrown OperationCanceledException that matched
                // neither catch above, logging the real exception instead of a fabricated one.
                LogError(ex);
                Interlocked.Increment(ref _factoryFailureStreak);
            }
            finally
            {
                // The old item was already disposed before ReplaceOne was enqueued, so any failure
                // above means the pool really shrank by one - without this, CurrentCapacity lies forever.
                if (!replaced)
                {
                    var current = CurrentCapacity;
                    Volatile.Write(ref _currentCapacity, current - 1);
                }
            }
        }

        private static async ValueTask DisposeItemAsync(T item)
        {
            if (item is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (item is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        #endregion

        #region background pumps

        private async Task RunHeartbeatAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(PulseHeartBeat, _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    LogMessage("Started Heart Beat item");
                    var acquired = await AcquireForHeartbeatAsync(_lifetime.Token).ConfigureAwait(false);
                    if (!acquired.Successful)
                    {
                        LogMessage("Heart Beat item not available");
                        continue;
                    }
                    using var pulseTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    pulseTimeout.CancelAfter(PulseHeartBeat);
                    // ADR007V03: the callback now receives the raw item and returns a bool instead
                    // of the whole RingBufferValue, removing the "do not dispose this yourself"
                    // trap by construction - there's no disposable object to misuse anymore.
                    // `false` is routed through the same Invalidate()/DisposeAsync() path any
                    // consumer's own Invalidate() call already uses - one mechanism, not two.
                    // Defaults to healthy (true) if BufferHeartBeat is somehow null here, which
                    // shouldn't happen given the startup gate above.
                    var heartbeatWork = Task.Run(() => BufferHeartBeat is null || BufferHeartBeat(acquired.Current));
                    try
                    {
                        var healthy = await heartbeatWork.WaitAsync(pulseTimeout.Token).ConfigureAwait(false);
                        if (!healthy)
                        {
                            acquired.Invalidate();
                            _heartbeatInvalidations.Add(1, new KeyValuePair<string, object?>("buffer.name", Name));
                            LogMessage("Heart Beat item invalidated - replacement will follow.");
                        }
                        // Unlike TurnbackAsync's general contract for an external caller's own
                        // DisposeAsync() call (a hang there is genuinely local to them, their own
                        // problem), "the caller" here is this pump itself. An unbounded await would
                        // let a single hanging Dispose() stall _heartbeatTask forever - and
                        // DisposeAsync() awaits _heartbeatTask via Task.WhenAll(pending) before its
                        // own cleanup (draining _availableItems, disposing
                        // _lifetime/_meter/_activitySource) runs, so the whole manager's shutdown
                        // would never complete, with _disposeGuard (already set) making a retry a
                        // permanent silent no-op. Bounded the same way the timeout branch below
                        // already bounds a stuck callback's own dispose: PulseHeartBeat as grace
                        // period, deferred into _pendingHeartbeatDisposals (drained by
                        // DisposeAsync's own cleanup) if it doesn't finish in time. TurnbackAsync
                        // already posts ReplaceOne before this dispose even starts, so capacity is
                        // corrected either way, regardless of how long Dispose() actually takes.
                        var disposeTask = acquired.DisposeAsync().AsTask();
                        try
                        {
                            await disposeTask.WaitAsync(PulseHeartBeat).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            // "Capacity was already corrected" only holds for the Invalidate
                            // (unhealthy) path, so it's left out of the message - it doesn't hold
                            // for every case that reaches here, and the reader doesn't need that
                            // detail to know the dispose is deferred.
                            LogWarning("Heart Beat item dispose did not complete within one pulse - deferring.");
                            // Deferring a continuation of disposeTask, not the raw task, matters:
                            // without observing the fault via a continuation first, a late failure
                            // from this specific dispose would reach neither LogError/OnError nor
                            // TaskScheduler.UnobservedTaskException, unlike every sibling deferred-
                            // dispose path in this file. Wrapping it in a ContinueWith that observes
                            // and logs the fault means DisposeAsync's own drain still waits for it.
                            var observedDispose = disposeTask.ContinueWith(t =>
                            {
                                if (t.IsFaulted) LogError(t.Exception!.GetBaseException());
                            }, TaskScheduler.Default);
                            lock (_pendingHeartbeatDisposalsGate)
                            {
                                _pendingHeartbeatDisposals.RemoveAll(t => t.IsCompleted);
                                _pendingHeartbeatDisposals.Add(observedDispose);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!heartbeatWork.IsCompleted)
                    {
                        // The callback is still running - either it blocked past its own pulse
                        // budget, or an ordinary shutdown cancelled _lifetime while it was still
                        // going. Either way it keeps running on its own thread-pool thread, since a
                        // blocking synchronous callback can't be forcibly cancelled, so it may
                        // still be reading/writing the resource right now. Two separate concerns,
                        // handled on two different timelines: replace the slot right away (via
                        // ReplaceOne directly, bypassing TurnbackAsync's combined dispose-then-
                        // replace) so capacity isn't lost while the callback runs, but defer
                        // actually disposing the stuck resource until the orphaned callback truly
                        // finishes. Disposing it now, while the callback might still be touching
                        // it, would be a use-after-dispose race on the caller's own object (a DB
                        // connection, a RabbitMQ channel). Its eventual outcome is still observed,
                        // so a late fault can't surface as an unobserved task exception.
                        if (!_lifetime.IsCancellationRequested)
                        {
                            LogError(new TimeoutException("Timeout Heart Beat"));
                        }
                        else
                        {
                            LogMessage("Heart Beat cancelled by shutdown, callback still running - deferring dispose.");
                        }
                        _commands.Writer.TryWrite(EngineCommand.ReplaceOne());
                        // .Unwrap() so the tracked Task actually completes when the inner await
                        // does, not merely when the async lambda is first scheduled - needed so
                        // DisposeAsync (below) can wait on real completion, not just on whether
                        // this continuation started.
                        var deferredDispose = heartbeatWork.ContinueWith(async t =>
                        {
                            if (t.IsFaulted) LogError(t.Exception!.GetBaseException());
                            try
                            {
                                await DisposeItemAsync(acquired.Current).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                LogError(ex);
                            }
                        }, TaskScheduler.Default).Unwrap();
                        // Prune already-finished entries before adding this one - otherwise a
                        // chronically slow HeartBeat callback (timing out on every single pulse)
                        // would grow this list for as long as the buffer runs, not just for as long
                        // as disposals are genuinely still in flight.
                        lock (_pendingHeartbeatDisposalsGate)
                        {
                            _pendingHeartbeatDisposals.RemoveAll(t => t.IsCompleted);
                            _pendingHeartbeatDisposals.Add(deferredDispose);
                        }
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        // heartbeatWork.IsCompleted was already true by the time this exception was
                        // observed (the sibling catch's guard above didn't match), so the callback
                        // is no longer touching the resource - disposing now is safe. The exception
                        // itself is still just an ordinary shutdown ending the wait, not a genuine
                        // failure - the same shutdown-vs-failure ambiguity guarded against
                        // elsewhere in this class.
                        LogMessage("Heart Beat cancelled by shutdown after the callback had already finished.");
                        await acquired.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                        await acquired.DisposeAsync().ConfigureAwait(false);
                    }
                    // Worded to describe what's always true - this pump's own iteration ended -
                    // rather than implying the item's processing/dispose finished, which isn't
                    // true on the branch above where the callback is still running and its dispose
                    // is deferred, not done.
                    LogMessage("Heart Beat pump iteration finished");
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
            catch (ObjectDisposedException)
            {
                // Same "manager disposed" shutdown as the OperationCanceledException case above.
                // There's a narrow window where DisposeAsync has already set _disposed but
                // _lifetime.Token hasn't yet observed cancellation; a heartbeat tick's own
                // internal acquire in that window throws this instead.
                //ignore: manager disposed
            }
        }

        private async Task RunSampleTickAsync()
        {
            try
            {
                await Task.Delay(SamplesBase, _lifetime.Token).ConfigureAwait(false);
                var delay = TimeSpan.FromMilliseconds(SamplesBase.TotalMilliseconds / SamplesCount);
                while (!_lifetime.IsCancellationRequested)
                {
                    // Skip enqueueing while a scale operation is in progress: the engine is a
                    // single serial consumer, so a Tick written now would just queue up behind the
                    // in-flight scale and run the instant it frees up - a burst of near-duplicate
                    // post-scale samples, not a time-spread window. _scaling is read here, its
                    // only reader, instead of in ProcessTick, where it was already always false by
                    // the time a queued Tick got processed.
                    if (!_scaling)
                    {
                        _commands.Writer.TryWrite(EngineCommand.Tick());
                    }
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
        }

        #endregion

        #region logging

        private void LogMessage(string message)
        {
            // The IsEnabled(Debug) check is necessary here, not just cosmetic. Without it, every
            // call would still build the interpolated string and a closure even when Debug
            // logging is off - and ProcessTick calls this roughly every SamplesBase/SamplesCount
            // interval (300ms by default) for the whole lifetime of every elastic buffer, not just
            // per scale operation.
            if (Logger is null || !SafeIsEnabled(Logger, LogLevel.Debug)) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            SafeInvokeSink(() => logMessageForDbg(Logger, Name, msg, null));
        }

        private void LogWarning(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            SafeInvokeSink(() => logMessageFoWrn(Logger, Name, msg, null));
        }

        private void LogError(Exception error)
        {
            if (Logger is null && ErrorHandler is null) return;
            if (ErrorHandler is null)
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {error.Message} ";
                SafeInvokeSink(() => logMessageForErr(Logger!, Name, msg, error));
            }
            else
            {
                SafeInvokeSink(() => ErrorHandler.Invoke(error));
            }
        }

        // A user-supplied Logger/ErrorHandler is untrusted external code: if it throws, that must
        // never be allowed to permanently break the heartbeat pump, leak a pooled item, make
        // DisposeAsync() itself throw, or otherwise escape into an unrelated core operation like
        // WarmupAsync/AcquireAsync. There's nothing further to log about the failure, since the
        // sink itself is what's broken - so this is a silent best-effort swallow, the same "must
        // never throw regardless of how a background pump ended" philosophy DisposeAsync's own
        // comment already states.
        private static void SafeInvokeSink(Action invoke)
        {
            try
            {
                invoke();
            }
            catch
            {
                //ignore: the logging/error sink itself threw - nothing further can be logged about it
            }
        }

        // Same untrusted-external-code rationale as SafeInvokeSink above: a user-supplied Logger
        // is untrusted, and IsEnabled itself can throw (Microsoft.Extensions.Logging's composite
        // Logger aggregates and rethrows provider exceptions, e.g. a provider disposed ahead of
        // this manager during host shutdown). An unguarded IsEnabled(Debug) check here, unlike
        // every other call into Logger/ErrorHandler in this class, could permanently kill
        // _engineTask or _heartbeatTask, since ProcessTick/RunHeartbeatAsync have no catch broad
        // enough to survive it. So a throwing IsEnabled is treated as "not enabled", skipping this
        // one Debug line instead of propagating.
        private static bool SafeIsEnabled(ILogger logger, LogLevel level)
        {
            try
            {
                return logger.IsEnabled(level);
            }
            catch
            {
                return false;
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageFoWrn = LoggerMessage.Define<string, string>(LogLevel.Warning, 0, "RingBufferManager({Source}) : {Message}");

        #endregion

        private enum EngineCommandKind { Warmup, Switch, ReplaceOne, Tick, FactoryBatchCompleted, RemovalBatchCompleted, Backlog }

        private sealed record EngineCommand
        {
            public required EngineCommandKind Kind { get; init; }
            public ScaleSwitch? Target { get; init; }
            public TimeSpan? PinDuration { get; init; }
            public TaskCompletionSource<bool>? Accepted { get; init; }
            public TaskCompletionSource<bool>? Completion { get; init; }
            public int Quantity { get; init; }
            public int Created { get; init; }
            public int Removed { get; init; }
            public Exception? Failure { get; init; }
            public string? ScaleTrigger { get; init; }

            public static EngineCommand Warmup(TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Warmup, Completion = completion };

            public static EngineCommand Switch(ScaleSwitch target, TimeSpan pinDuration, TaskCompletionSource<bool> accepted, TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Switch, Target = target, PinDuration = pinDuration, Accepted = accepted, Completion = completion };

            public static EngineCommand Backlog() => new() { Kind = EngineCommandKind.Backlog };

            public static EngineCommand ReplaceOne() => new() { Kind = EngineCommandKind.ReplaceOne };

            public static EngineCommand Tick() => new() { Kind = EngineCommandKind.Tick };

            public static EngineCommand FactoryBatchCompleted(
                int quantity, int created, Exception? failure, TaskCompletionSource<bool>? completion, string scaleTrigger) =>
                new()
                {
                    Kind = EngineCommandKind.FactoryBatchCompleted,
                    Quantity = quantity,
                    Created = created,
                    Failure = failure,
                    Completion = completion,
                    ScaleTrigger = scaleTrigger,
                };

            public static EngineCommand RemovalBatchCompleted(int quantity, int removed, TaskCompletionSource<bool>? completion) =>
                new()
                {
                    Kind = EngineCommandKind.RemovalBatchCompleted,
                    Quantity = quantity,
                    Removed = removed,
                    Completion = completion,
                };
        }
    }
}
