// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

// Design note (ADR001/ADR005): all mutable scale state (_currentCapacity, fault counters,
// samples) is owned exclusively by the single consumer loop (RunEngineAsync). No other thread
// ever mutates it, so no lock/semaphore is needed for correctness. Callers only ever post
// commands into an unbounded Channel<EngineCommand> and, optionally, await a completion signal.
//
// Two deliberate simplifications versus v4, both authorized by ADR006 (no compatibility
// obligation) and consistent with ADR001's "correctness by construction over blocking dances":
//  - AcquireAsync never blocks waiting for an in-flight scale operation. It always reads
//    directly from the available-items channel (which blocks only until an item exists),
//    regardless of LockWhenScaling. LockWhenScaling now controls exactly one thing: whether
//    SwitchToAsync's caller awaits the scale operation's completion before returning.
//  - AcquireDelayAttempts is removed: a Channel-based read has no polling loop to pace.
//  - Warmup is a Lazy<Task> (ExecutionAndPublication) so concurrent callers de-duplicate into one
//    attempt (v4's retry path was itself broken: a failed Startup() left _WarmupRunning stuck true
//    forever). A failed attempt is no longer cached forever (see ADR011): an explicit WarmupAsync()
//    call after a failure installs a fresh attempt (CAS on _warmup) and retries, so a transient
//    factory failure at startup no longer bricks the instance permanently. AcquireAsync/
//    SwitchToAsync's implicit warmup trigger (EnsureWarmupAsync) does NOT auto-retry - it only
//    observes whatever the latest attempt's outcome is, so ordinary acquire traffic against a
//    still-broken factory cannot turn into a retry storm; retrying is always a deliberate,
//    caller-initiated WarmupAsync() call.
//
// Observability (ADR008): _meter and _activitySource are per-instance, not static, and both
// share the constant Name "RingBufferPlus" - a listener subscribing to that name still sees
// every live buffer, but disposing one buffer's Meter/ActivitySource (in DisposeAsync) can never
// silence another's. Both APIs are "pay for play": with no listener attached, Add/Record/
// StartActivity calls are near-zero-cost, so this always runs, unconditionally - there is no
// opt-in/opt-out on the builder surface. Warmup's own MoveToCapacityAsync call intentionally
// carries no scaleTrigger (see below) - the initial fill is not a "scale operation" in the
// manual/auto sense the scale.* metrics describe.

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
        // Round 3 (Complexidade/Desempenho, v6 pre-release audit): cached once instead of a fresh
        // method-group-to-delegate conversion on every successful AcquireAsync (the conversion
        // captures `this` and cannot be cached by the compiler itself since TurnbackAsync is an
        // instance method) - a small, semantically-inert allocation removed from the one path that
        // runs per request, not per scale operation.
        private readonly Func<RingBufferValue<T>, ValueTask> _turnbackDelegate;

        // Monitor's sliding demand window (ADR001V03/ADR003V03) - bounded to SamplesCount, engine-
        // thread-only (only ProcessTick and the scale-completion cleanup paths touch it).
        private readonly List<int> _samples = [];

        // Monitor (ADR001V03/ADR003V03): whether the LAST TICK THAT ACTUALLY RAN observed demand
        // keeping pace with or exceeding capacity ("active"). Verified empirically (a temporary
        // per-tick counter, run across the full test suite): this is true far less often than the
        // validated simulation's own "active" predicate, because Tick is skipped entirely while
        // _scaling is true (see the Tick case's own comment) - and any real backlog large enough to
        // make demand >= capacity almost always already has a Fábrica batch in flight by the time
        // Tick would otherwise run, from EvaluateBacklogReactive's own immediate (non-Tick-cadenced)
        // dispatch. The only realistic path where a Tick actually observes active=true is a buffer
        // pinned at MaxCapacity with genuine backlog (EvaluateBacklogReactive early-returns there
        // without dispatching, so _scaling never blocks Tick) - narrow, and moot for scaling up
        // (already at Max), but still meaningful for the window once that backlog eventually clears
        // and a scale-down needs a clean read.
        //
        // MoveToCapacityAsync's and FactoryBatchCompleted's own finally blocks already
        // unconditionally clear _samples after any scale operation completes (F6, pre-existing
        // before this Monitor work) - but only a scale operation clears it, and whether one fires
        // at all is gated by MonitorDeadband. During a genuinely STEADY period - demand stable,
        // capacity already matching it, every computed target landing inside the deadband - no
        // scale operation ever fires, so nothing clears the window: it fills to SamplesCount one
        // sample per tick and then slides, same as the algorithm's own design intends for ordinary
        // operation. If demand THEN drops during that steady period, the new low samples must
        // outvote a full window of stale higher ones before the percentile/trend reflects reality -
        // see numberSamples' own XML doc for what that means at the shipped default. This field and
        // the pre-existing per-scale-op clears only shorten that lag when an actual burst/backlog
        // episode (not a quiet steady period) precedes the drop. Engine-thread-only, like
        // _currentCapacity itself - only ProcessTick reads or writes it.
        private bool _monitorActive;

        private readonly Meter _meter = new("RingBufferPlus");
        private readonly ActivitySource _activitySource = new("RingBufferPlus");
        private readonly Histogram<double> _acquireDuration;
        private readonly Counter<long> _acquireFaults;
        private readonly Counter<long> _scaleOperations;
        private readonly Histogram<double> _scaleDuration;

        private Task? _heartbeatTask;
        private Task? _sampleTickTask;

        // Deferred dispose continuations from an orphaned heartbeat callback (F12/F15) - added
        // only from RunHeartbeatAsync's own loop, so by the time _heartbeatTask (awaited in
        // DisposeAsync before this bag is snapshotted) has completed, no more entries can arrive.
        private readonly ConcurrentBag<Task> _pendingHeartbeatDisposals = new();

        // Round 1 (Estabilidade, v6 pre-release audit): volatile, not a plain bool - written once,
        // synchronously, as the first instruction of DisposeAsync, then read from other threads
        // (AcquireCoreAsync, SwitchToAsync, WarmupAsync/WarmupCoreAsync) with no other memory
        // barrier guaranteeing its visibility to them.
        private volatile bool _disposed;
        private int _disposeGuard;
        private int _currentCapacity;
        private volatile bool _scaling;

        // Manual pin (ADR007V03): while set in the future, a successful SwitchToAsync's own target
        // substitutes for the Monitor's predictive output - Tick is skipped entirely (see the Tick
        // case's own comment), the same way it already is while _scaling is true, so the Monitor
        // neither observes nor overrides the pinned capacity for this long. Never consulted by
        // EvaluateFloorGuard or EvaluateBacklogReactive (ADR007V03: a pin never suppresses either -
        // both keep running on their own independent triggers, untouched by this field). Engine-
        // thread-only, like _currentCapacity itself - only the Switch case writes it, only the Tick
        // case reads it.
        private DateTime? _pinExpiresAt;

        // Backlog-reactive signal (ADR001V03): count of callers currently blocked in
        // AcquireCoreAsync waiting for an item - mutated via Interlocked from any caller thread
        // (unlike _currentCapacity, this is not sole-owner state, just a shared counter), read by
        // the engine thread in EvaluateBacklogReactive.
        private int _waitingCount;

        // Floor guard (ADR001V03): the instant CurrentCapacity < MinCapacity was first detected,
        // engine-thread-only state (like _currentCapacity itself). Set once on first detection and
        // left untouched across retries - only cleared once the breach actually resolves - so the
        // grace window (FactoryTimeout, reused per the ADR) measures time since the breach started,
        // not since the most recent retry attempt.
        private DateTime? _floorBreachDetectedAt;

        // Fábrica (ADR001V03): the currently in-flight background scale-up batch (DispatchScaleUp),
        // if any - only ever written by the engine thread (single consumer), and only ever one at
        // a time (Switch rejects, and EvaluateBacklogReactive skips, a new dispatch while _scaling
        // is true). DisposeAsync
        // reads this only after _engineTask has already been awaited to completion (so no further
        // write can race it) and awaits it too, so a batch still in flight at shutdown still gets
        // to record its telemetry and resolve its caller before DisposeAsync returns - see the
        // ScaleUp_*RacedByDisposeAsync observability tests.
        //
        // This field can be overwritten by a new dispatch before the OLD batch's Task.Run lambda
        // has fully returned (the engine clears _scaling - unblocking a new dispatch - as soon as
        // it processes that batch's own FactoryBatchCompleted command, which the lambda posts as
        // its very last statement before returning) - only the newest batch's task is ever tracked,
        // not a list of all of them. This is safe: by the time a batch's completion command has
        // been posted (the trigger for _scaling to clear and a new dispatch to become possible),
        // that batch has nothing observable left to do - TryWrite is its last real action, so an
        // overwritten reference is never "still doing work" that DisposeAsync then fails to wait for.
        private Task? _factoryBatchTask;

        // Remoção (ADR001V03): the currently in-flight background scale-down disposal batch
        // (DispatchScaleDown), if any - same shape, same guarantees, and same "only one batch (of
        // either kind) in flight at a time via _scaling" invariant as _factoryBatchTask above.
        // Isolated from the engine thread for the same reason Fábrica's creation execution already
        // is: Dispose()/DisposeAsync() on a real connection can block on I/O just as a factory call
        // can, and before this role existed, RemoveItemsAsync's own disposal (bounded by
        // PulseHeartBeat, the pre-existing N1/N2 grace-period mitigation) still ran inline on the
        // engine's single-consumer thread - so even a bounded hang there still delayed every other
        // command (a floor-guard replenishment, an unrelated ReplaceOne, ...) queued behind it for
        // up to that same grace period. DisposeAsync awaits this the same way, right alongside
        // _factoryBatchTask.
        private Task? _removalBatchTask;

        // Fábrica (ADR001V03): a simple growing backoff after consecutive genuine factory
        // failures - self-protection against hammering a broken factory, not a circuit-breaker
        // state machine. Persisted on the manager (not scoped to one CreateItemsAsync/
        // CreateSingleReplacementAsync call) so a factory that stays broken across several
        // separate creation attempts (repeated floor-guard cycles, repeated backlog-reactive
        // requests, etc.) is throttled progressively over time, not just within a single batch -
        // that cross-call repetition, not a single batch's own bounded concurrency, is the
        // hammering scenario this exists to soften. Reset to zero by any genuine success;
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
        /// Fábrica role). Bounds a large batch (warmup, scale-up, or floor-guard replenishment) from
        /// flooding a struggling-but-technically-accepting downstream with simultaneous creation
        /// attempts - the thundering-herd mitigation. Defaults to <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/>
        /// so a direct object-initializer construction that omits it (e.g. in tests) never
        /// silently deadlocks CreateItemsAsync's gate at a zero-permit semaphore.
        /// </summary>
        public int MaxConcurrentFactoryCalls { get; init; } = RingBufferDefault.MaxConcurrentFactoryCalls;

        public TimeSpan PulseHeartBeat { get; init; }

        public TimeSpan SamplesBase { get; init; }

        public int SamplesCount { get; init; }

        /// <summary>
        /// True for an elastic pool (ADR001V03/ADR007V03): the floor guard, backlog-reactive
        /// signal, and Monitor are all unconditionally active whenever this is true, and inactive
        /// (Monitor: not even started; backlog-reactive: gated off) when this is false - a fixed
        /// pool has nothing to scale. There is no further "automatic vs. manual" split within an
        /// elastic pool; <see cref="SwitchToAsync(ScaleSwitch, TimeSpan)"/> is a temporary pin over
        /// the same always-on Monitor, not an alternative mode.
        /// </summary>
        public bool Elastic { get; init; }

        /// <summary>
        /// The Monitor's predictive autoscale algorithm parameters (ADR003V03). Defaulted here
        /// (not just on the builder) so a direct object-initializer construction that omits them
        /// (e.g. in tests) still runs the algorithm with the ADR's own defaults, the same reasoning
        /// as <see cref="MaxConcurrentFactoryCalls"/>'s own default.
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
            _scaleOperations = _meter.CreateCounter<long>("ringbufferplus.scale.operations", description: "Count of scale-up/scale-down operations, tagged by direction, trigger, and success.");
            _scaleDuration = _meter.CreateHistogram<double>("ringbufferplus.scale.duration", unit: "s", description: "Duration of scale-up/scale-down operations, in seconds, tagged by direction, trigger, and success.");
            _meter.CreateObservableGauge("ringbufferplus.capacity.current",
                () => new Measurement<int>(CurrentCapacity, new KeyValuePair<string, object?>("buffer.name", Name)),
                description: "Current capacity of the buffer.");

            _engineTask = Task.Run(RunEngineAsync);
        }

        public ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default) =>
            AcquireCoreAsync(countsTowardFaultBudget: true, cancellation);

        // R11: the heartbeat pump's own internal acquire (see RunHeartbeatAsync) is a health
        // check, not consumer demand - counting its timeout toward the autoscale fault budget is
        // a self-inflicted false signal. Genuine external demand still faults normally through
        // the public AcquireAsync above; this only exempts the heartbeat's own pulse.
        private ValueTask<RingBufferValue<T>> AcquireForHeartbeatAsync(CancellationToken cancellation) =>
            AcquireCoreAsync(countsTowardFaultBudget: false, cancellation);

        // Round 4 (Resiliência, v6 pre-release audit): _disposed is set synchronously as the very
        // first step of DisposeAsync, but _lifetime.Dispose() only runs at the end of its finally,
        // after awaiting in-flight engine/heartbeat/sample-tick work - a TOCTOU window between a
        // caller's ObjectDisposedException.ThrowIf(_disposed, this) guard passing and its next
        // _lifetime.Token access. CancellationTokenSource.Token's getter (and CreateLinkedTokenSource
        // over it) throws ObjectDisposedException on an already-disposed source regardless of this
        // check, but with ObjectName pointing at CancellationTokenSource instead of this manager -
        // still the right exception type, just a confusing identity for anyone reading it as an
        // unrelated internal failure during an otherwise-ordinary graceful shutdown. Since _disposed
        // flips to true strictly before _lifetime is ever disposed, catching that specific case here
        // and re-throwing through the same guard restores the identity a caller already expects.
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
            await EnsureWarmupAsync().ConfigureAwait(false);

            using var activity = _activitySource.StartActivity("RingBufferPlus.Acquire");
            activity?.SetTag("buffer.name", Name);

            var startTimestamp = Stopwatch.GetTimestamp();
            // Round 3 (v6 pre-release audit): measured (auditoria-desempenho) at ~416 of ~600 B/op
            // on the uncontended fast path - the CTS/timer here is built and torn down even when
            // the immediate TryRead below succeeds and this timeout is never actually consulted.
            // Deliberately left as eager construction, not made lazy (only build it in the else
            // branch, once the fast path has already failed): doing so would require hoisting
            // timeoutCts/linked out of that branch's scope so the catch blocks below can still
            // inspect timeoutCts.IsCancellationRequested, replacing the clean `using var` pattern
            // with manual disposal in a finally, and replacing the pre-fast-path
            // `!linked.IsCancellationRequested` check with one against the raw cancellation/
            // _lifetime tokens instead - in a method whose comments already document a long history
            // of subtle cancellation-correctness fixes (R11/R15/R17/O1/O2/O6). The allocation here
            // is real but not the actual bottleneck for this library's use case (pooling
            // network/DB resources, where Factory's own I/O dominates by orders of magnitude) -
            // not worth the risk of reopening one of those classes of bug for it. Same "keep as-is"
            // trade-off shape as the 4x amplification decision in CreateItemsAsync.
            using var timeoutCts = new CancellationTokenSource(AcquireTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, LifetimeToken(), cancellation);
            var isWaiting = false;
            try
            {
                T item;
                // linked.IsCancellationRequested is checked before the fast path so an already
                // (or immediately) cancelled token still surfaces its genuine cancellation via
                // ReadAsync below, exactly as before this fast path existed - a bare TryRead
                // observes no token at all, and would otherwise let an available idle item mask a
                // caller's own cancellation just because the pool happened to be non-empty.
                if (!linked.IsCancellationRequested && _availableItems.Reader.TryRead(out var immediate))
                {
                    item = immediate;
                }
                else
                {
                    // Backlog-reactive signal (ADR001V03): a caller that cannot be served right
                    // away is genuine, real-time demand - report it to the Orquestrador now,
                    // before AcquireTimeout has any chance to elapse (the old fault-count trigger
                    // below only reacts after that timeout already happened). Same R11 reasoning
                    // as the fault path: the heartbeat's own internal acquire is a health check,
                    // not consumer demand, so it must not count here either.
                    Interlocked.Increment(ref _waitingCount);
                    isWaiting = true;
                    if (Elastic && countsTowardFaultBudget)
                    {
                        _commands.Writer.TryWrite(EngineCommand.Backlog());
                    }
                    item = await _availableItems.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                    // Decrement right here, not in the shared finally below: this caller is no
                    // longer waiting the instant it actually has an item, not after the telemetry
                    // and RingBufferValue construction that follow. A later EvaluateBacklogReactive
                    // call (e.g. from FactoryBatchCompleted, on another thread) reading _waitingCount
                    // in between would otherwise still see this already-served caller as backlog -
                    // narrowing that window is what keeps the documented approximation in
                    // EvaluateBacklogReactive small in practice, not just tolerated in theory.
                    Interlocked.Decrement(ref _waitingCount);
                    isWaiting = false;
                }
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                // Round 4 (Observabilidade, v6 pre-release audit): acquire.timed_out/acquire.cancelled
                // are always emitted (false here) rather than only on the failure rows below, for the
                // same reason DispatchScaleDown's tag contract already documents for scale.* - a
                // consumer filtering acquire.timed_out="false" would otherwise get zero rows for every
                // successful acquire (the label simply wouldn't exist), not the "calls without a
                // timeout" they'd expect.
                _acquireDuration.Record(elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", true),
                    new KeyValuePair<string, object?>("acquire.timed_out", false),
                    new KeyValuePair<string, object?>("acquire.cancelled", false));
                activity?.SetTag("success", true);
                activity?.SetTag("timed_out", false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new RingBufferValue<T>(Name, elapsed, true, item, _turnbackDelegate);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                var timedOut = timeoutCts.IsCancellationRequested;
                if (timedOut)
                {
                    // Fault-count-based autoscale triggering is retired (ADR001V03: the backlog-
                    // reactive signal replaces it) - by the time a genuine AcquireTimeout is even
                    // possible, this same caller already reported itself as backlog the moment it
                    // started waiting (see the TryRead fast path above), well before this point.
                    // acquire.faults below remains a pure diagnostic counter, unrelated to triggering.
                    LogWarning("RingBuffer without resource");
                    _acquireFaults.Add(1, new KeyValuePair<string, object?>("buffer.name", Name));
                }
                // acquire.timed_out mirrors the activity's own "timed_out" tag (Round 5,
                // Observabilidade - finding O6): without it, this histogram's failed rows can't be
                // told apart from an ordinary shutdown/caller-cancellation, same gap O1/O2 already
                // closed on the metrics/activity side of Scale/Acquire elsewhere.
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                _acquireDuration.Record(elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", false),
                    new KeyValuePair<string, object?>("acquire.timed_out", timedOut),
                    new KeyValuePair<string, object?>("acquire.cancelled", false));
                activity?.SetTag("success", false);
                activity?.SetTag("timed_out", timedOut);
                // Only a genuine timeout is a health signal worth an Error status (Round 4,
                // Observabilidade - finding O2) - reaching this catch without timedOut means
                // _lifetime (an ordinary shutdown) is what ended the wait instead, same
                // distinction R15/F15/R17/O1 already make elsewhere.
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
                    new KeyValuePair<string, object?>("acquire.cancelled", true));
                activity?.SetTag("success", false);
                activity?.SetTag("timed_out", false);
                activity?.SetTag("cancelled", true);
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
                    // Round 8 (Resiliência Finding 3): nobody awaits completion.Task on this
                    // unlocked path - if the engine loop later resolves it via TrySetException on a
                    // genuine scale failure, that fault becomes a genuinely unobserved task
                    // exception once this TCS is garbage-collected. Merely attaching a
                    // continuation does not mark the exception observed - reading .Exception
                    // inside it is what actually does that.
                    _ = completion.Task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return true;
                }
                return await completion.Task.WaitAsync(LifetimeToken()).ConfigureAwait(false);
            }
            // R24 (Round 7, Resiliência): only an ordinary shutdown of this buffer's own lifetime
            // resolves to a plain `false` here - a raw OperationCanceledException/TaskCanceledException
            // thrown by the factory itself (e.g. an HttpClient/gRPC/DB-driver's own unrelated internal
            // timeout, surfaced as-is per the deliberate zero-progress contract confirmed in Round 6)
            // must not be swallowed into an indistinguishable, exception-less `false` - it falls
            // through this guard and propagates to the caller like any other genuine factory exception.
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
                    // Disposal must be best-effort and never throw regardless of how a background
                    // pump ended, but an unexpected fault here is still worth surfacing through the
                    // configured logger/ErrorHandler rather than being fully silent. No separate
                    // OperationCanceledException catch is needed here (removed as dead code during
                    // the between-rounds backlog cleanup, 2026-08-21): RunEngineAsync,
                    // RunHeartbeatAsync, and RunSampleTickAsync each already fully own their own
                    // OperationCanceledException at their own outermost level, so none of the tasks
                    // in `pending` can ever propagate one here - this catch-all covers whatever else
                    // might, same as before.
                    LogError(ex);
                }

                // The engine loop has now fully stopped (awaited above as part of `pending`), so
                // _factoryBatchTask/_removalBatchTask can no longer be reassigned by a new
                // DispatchScaleUp/DispatchScaleDown call - safe to read both here. A Fábrica or
                // Remoção batch still in flight at shutdown must still be waited on: without this,
                // its telemetry (activity/meter, recorded inside the batch itself) could be
                // recorded after this method has already returned, or its caller's Completion
                // never resolved at all.
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
                // loop can no longer add to this bag - safe to snapshot now (Round 4, Estabilidade).
                // Without this wait, DisposeAsync could return while a resource an orphaned
                // heartbeat callback (F12/F15) was still holding had not yet actually been
                // disposed - a real leak if the process exits shortly after DisposeAsync returns,
                // not merely a delay.
                if (!_pendingHeartbeatDisposals.IsEmpty)
                {
                    var deferredDisposals = _pendingHeartbeatDisposals.ToArray();
                    try
                    {
                        // Bounded, not indefinite: the orphaned callback that owns these can never
                        // be forcibly cancelled, so it may still be permanently hung. PulseHeartBeat
                        // is reused as the grace period - the same budget the pump itself already
                        // gives a single heartbeat cycle.
                        await Task.WhenAll(deferredDisposals).WaitAsync(PulseHeartBeat).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Warning, not Debug (Round 5, Observabilidade, finding O8): this is not an
                        // ordinary shutdown-vs-failure case like the sibling messages elsewhere -
                        // a resource is now leaked for an indeterminate time past this DisposeAsync()
                        // call, comparably significant to the existing "RingBuffer without resource"
                        // LogWarning.
                        // Round 2 (Estabilidade, v6 pre-release audit): "orphaned callback(s)"
                        // used to be accurate for every entry here, but _pendingHeartbeatDisposals
                        // now also receives entries whose callback already returned a verdict and
                        // it's only the item's own Dispose() still running - reworded to cover both
                        // without implying the callback itself is still orphaned in every case.
                        LogWarning($"DisposeAsync did not wait for {deferredDisposals.Length} pending heartbeat item dispose(s) still running past the grace period - their resource(s) will be disposed once/if they finish, but not before this DisposeAsync() call returned.");
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
                // Defensive, per-item (Round 5, Estabilidade, Finding A): a raw loop calling
                // DisposeItemAsync directly would abort on the first item whose Dispose() throws,
                // leaking every remaining item plus _lifetime/_meter/_activitySource below -
                // permanently, since _disposeGuard makes a second DisposeAsync() call a silent
                // no-op. This is the exact same failure mode R2 already fixed for the warmup-
                // exception trigger; DisposeItemsDefensivelyAsync (also used by DispatchScaleDown's
                // Remoção batch) is the same fix applied to this trigger.
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
            // R23 (Round 7, Resiliência): only an ordinary shutdown of this buffer's own lifetime
            // resolves to reached=false here - a raw OperationCanceledException/TaskCanceledException
            // thrown by the factory itself (e.g. an HttpClient/gRPC/DB-driver's own unrelated internal
            // timeout) must not be swallowed into the generic "did not reach initial capacity" message
            // below, discarding the real exception. It falls through this guard and propagates as-is,
            // same as any other genuine factory exception on a zero-progress batch (Round 6).
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                reached = false;
            }

            if (!reached)
            {
                var err = new InvalidOperationException("RingBuffer did not reach initial capacity");
                // An ordinary DisposeAsync() racing this warmup is not a genuine capacity failure - it
                // surfaces as the very same "reached = false" (MoveToCapacityAsync's own CreateItemsAsync
                // call absorbs the cancellation internally rather than throwing it - see R15/CreateItemsAsync
                // - and simply returns a partial/zero count, so ProcessCommandAsync's Warmup case resolves
                // the command's completion with TrySetResult(false) directly, not via an exception). Logging
                // it as an ERROR would mislead an on-call engineer into thinking the factory was unhealthy
                // during a clean shutdown (same bug class as R15).
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
                    // v6.0.0/ADR001V03 pre-work (Round 8 blast-radius sweep): enqueue the
                    // replacement BEFORE awaiting the old item's own Dispose()/DisposeAsync(), not
                    // after in a `finally`. That ordering meant a Dispose() that hangs forever left
                    // this `finally` unreached (an unfinished await never lets it run), so the slot
                    // was never replaced and CurrentCapacity stayed wrong forever - same shape of
                    // bug as F27/F28/F29, a third call site those fixes did not touch. Pool-wide
                    // capacity truthfulness must not depend on how long, or whether, a caller-owned
                    // item's Dispose() ever returns - that call hanging is this caller's own
                    // DisposeAsync() call hanging too (a local problem for them), not a reason for
                    // shared pool state to go wrong for everyone else. The exception (if any) from
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
                    // _scaling here means "a Fábrica-or-Remoção batch dispatched by a previous
                    // Switch, backlog-reactive, floor-guard, or Monitor evaluation is still in
                    // flight" (ADR001V03): with that batch now running in the background instead of
                    // blocking this loop, a second overlapping dispatch (of either kind) could each
                    // read a stale CurrentCapacity and independently mutate it on top of that stale
                    // read, over- or under-shooting [MinCapacity, MaxCapacity]. Rejecting here keeps
                    // "at most one batch (create or remove) at a time" and preserves the existing
                    // at-most-one-winner contract for concurrent callers requesting the same target
                    // (advisor review). A caller-visible consequence once Remoção's own disposal can
                    // take real time (was already true for Fábrica's factory calls): a second Switch
                    // arriving while a scale-down's disposal is still in flight is now rejected for
                    // as long as that disposal takes (bounded by PulseHeartBeat), not just for the
                    // near-instant dequeue that used to be the entire scale-down operation.
                    if (target == CurrentCapacity || _scaling)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                        break;
                    }
                    cmd.Accepted?.TrySetResult(true);
                    // Manual pin (ADR007V03): only set once a real scale is actually dispatched, not
                    // on the already-at-target rejection above - a pin's whole purpose is holding a
                    // capacity against the Monitor, which is moot if this call never changed
                    // anything. Set before dispatching, not after: the pin covers the dispatched
                    // batch's own in-flight time too, not just the moment after it resolves.
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
                        // Defensive re-check (RunSampleTickAsync's own check happens at write-time,
                        // before this command was even enqueued - a narrow window between that check
                        // and this command being dequeued could otherwise let the Monitor's own
                        // scale-up or scale-down run here while another batch is still in flight, see
                        // the Switch case above for why that risks an overshoot). This also skips
                        // ProcessTick's own _samples.Add for this tick, not just the scale
                        // decision - but that has no observable effect: FactoryBatchCompleted/
                        // RemovalBatchCompleted unconditionally clear _samples once the in-flight
                        // batch finishes (F6, same as MoveToCapacityAsync's own finally block
                        // already did), so any sample collected during the batch's whole in-flight
                        // window - however long that now is, no longer just one synchronous call -
                        // would be discarded anyway.
                        break;
                    }
                    if (_pinExpiresAt is { } pinExpiresAt)
                    {
                        // Manual pin (ADR007V03): substitutes for the Monitor's own predictive output
                        // for the pin's own duration, so Tick is skipped here too, same as while
                        // _scaling is true and for the same reason (see above) - any sample collected
                        // during the pin would only feed a decision that must not run yet anyway.
                        // Floor guard and backlog-reactive are NEVER gated by this (ADR007V03: a pin
                        // never suppresses either) - they run from their own independent triggers
                        // (ReplaceOne, Backlog, FactoryBatchCompleted, RemovalBatchCompleted), never
                        // from Tick, so this check cannot affect them.
                        if (DateTime.UtcNow < pinExpiresAt)
                        {
                            break;
                        }
                        _pinExpiresAt = null;
                    }
                    ProcessTick();
                    break;

                case EngineCommandKind.FactoryBatchCompleted:
                    // Telemetry (activity/meter) was already finalized inside DispatchScaleUp's own
                    // background task, unconditionally - see its comment for why. This case only
                    // applies the sole-owner state changes: capacity, _scaling, and the caller's
                    // completion.
                    //
                    // Clear in-flight status BEFORE resolving the caller's completion (advisor
                    // review): a sequential caller that awaits completion (LockWhenScaling) must never
                    // observe _scaling still true once its own await returns, or its very next
                    // SwitchToAsync could be spuriously rejected by the check above.
                    _scaling = false;
                    if (cmd.Created > 0)
                    {
                        // Applied against the live CurrentCapacity at completion time, not the value
                        // captured at dispatch - with batches serialized (one in flight at a time)
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
                    // Floor guard first (ADR001V03's own signal priority: floor guard >
                    // backlog-reactive > manual pin > Monitor) - a batch that only partially
                    // created its target (or a floor-guard batch that failed outright) can still
                    // leave CurrentCapacity below MinCapacity, and only one batch may be in flight
                    // at a time, so this is also where a still-unresolved breach gets its next
                    // retry. Dispatching here sets _scaling back to true, which makes the
                    // EvaluateBacklogReactive call below a no-op for this round - the floor
                    // legitimately wins the single in-flight-batch slot over ordinary backlog
                    // demand, not an oversight.
                    EvaluateFloorGuard();
                    // Re-evaluate backlog right away, regardless of what triggered this batch: any
                    // callers that started waiting while this one was in flight (and so had their own
                    // Backlog command skipped below, since only one batch may be in flight at a time)
                    // must not have to wait for their own AcquireTimeout to be addressed - see
                    // EvaluateBacklogReactive's own remarks for why netting-out in-flight-creating
                    // reduces to this deferred re-check under that one-at-a-time rule.
                    EvaluateBacklogReactive();
                    break;

                case EngineCommandKind.RemovalBatchCompleted:
                    // Telemetry (activity/meter) was already finalized inside DispatchScaleDown's
                    // own background task, unconditionally - see its comment for why. This case
                    // only applies the sole-owner state changes: capacity, _scaling, and the
                    // caller's completion. Same ordering rationale as FactoryBatchCompleted:
                    // _scaling clears BEFORE the caller's completion resolves, so a sequential
                    // caller (LockWhenScaling) never observes it still true once its own await
                    // returns.
                    _scaling = false;
                    if (cmd.Removed > 0)
                    {
                        // Confirmed completion, not the request (ADR001V03) - the items were
                        // already gone from _availableItems the instant DispatchScaleDown dequeued
                        // them, but CurrentCapacity only reflects that once Remoção's own disposal
                        // has actually finished, same "only the sole owner mutates, only on
                        // confirmed completion" rule Fábrica's own capacity increment follows.
                        Volatile.Write(ref _currentCapacity, CurrentCapacity - cmd.Removed);
                    }
                    var scaledDown = cmd.Removed == cmd.Quantity;
                    _samples.Clear();
                    // No Failure/exception path here, unlike FactoryBatchCompleted: a scale-down
                    // can only ever fully or partially succeed (DispatchScaleDown's own comment),
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
        // backlog-reactive > manual pin > Monitor). Protects the pool's minimum contractual floor
        // - CurrentCapacity actually dropping below MinCapacity - which today can only happen via
        // CreateSingleReplacementAsync's own finally block (a failed heartbeat- or
        // Invalidate()-triggered replacement), see its own comment. Warmup cannot silently leave
        // this gap: WarmupCoreAsync already throws "did not reach initial capacity" on any
        // shortfall, so the caller learns synchronously and no background guard is needed there.
        //
        // Undebounced by design (per the ADR): every evaluation that finds a breach dispatches
        // immediately, pacing coming only from Fábrica's own existing consecutive-failure backoff
        // (CreateItemsAsync), not from anything added here. There is no public "genuinely below
        // minimum" property yet - that surface is deferred to ADR007V03, same as
        // AutoScaleAcquireFault's numberOfFaults - so until it exists, a grace window that has
        // elapsed (FactoryTimeout, reused per the ADR, no new configuration surface) is reported
        // via LogError, the closest honest substitute available today.
        private void EvaluateFloorGuard()
        {
            var deficit = FloorGuardDecision.EvaluateBreach(CurrentCapacity, MinCapacity);
            if (deficit is null)
            {
                _floorBreachDetectedAt = null;
                return;
            }
            _floorBreachDetectedAt ??= DateTime.UtcNow;
            if (FloorGuardDecision.HasGraceWindowElapsed(_floorBreachDetectedAt.Value, DateTime.UtcNow, FactoryTimeout))
            {
                LogError(new InvalidOperationException($"RingBuffer below minimum capacity for longer than one FactoryTimeout cycle: current={CurrentCapacity}, minimum={MinCapacity}."));
            }
            if (_scaling)
            {
                return;
            }
            DispatchScaleUp(MinCapacity, scaleTrigger: "floor", completion: null);
        }

        // Backlog-reactive signal (ADR001V03): reacts to real, currently-waiting callers instead
        // of a coarse fault count, before any AcquireTimeout elapses, proportional to the actual
        // unmet demand - waiting callers minus what can already serve them (idle items). Replaces
        // the old fault-count-based trigger entirely (removed: EngineCommandKind.Fault, _faultCount,
        // and, once ADR007V03's public-surface pass landed, AutoScaleAcquireFault/NumberFault too -
        // this signal is simply always active for an elastic pool, gated only by Elastic below).
        //
        // The ADR's own formula also nets out "in-flight-creating" so overlapping backlog waves
        // never duplicate a request; here that term is always zero by construction: a batch already
        // in flight (from this signal or Switch) means _scaling is true, and this method returns
        // before computing a gap at all, deferring to the follow-up call this method's own caller
        // makes once FactoryBatchCompleted clears _scaling - any residual backlog gets addressed
        // then, without ever risking two overlapping batches reading a stale CurrentCapacity
        // independently (the same overshoot risk the Switch case's one-at-a-time rule protects
        // against).
        private void EvaluateBacklogReactive()
        {
            if (!Elastic || _scaling || CurrentCapacity >= MaxCapacity)
            {
                return;
            }
            // Known, accepted approximation ("simple, not exact" - same spirit as the
            // consecutive-failures counter under concurrency, ADR001V03): _waitingCount is
            // decremented in the served caller's own continuation (AcquireCoreAsync, immediately
            // after ReadAsync returns - deliberately not in that method's shared finally, to keep
            // this window as narrow as possible), which still runs asynchronously with respect to
            // this method's own caller. A follow-up evaluation (from FactoryBatchCompleted, right
            // after a batch completes) can therefore still, occasionally, see a just-served caller
            // as "waiting" a moment longer than reality, computing a gap that is briefly too high
            // and dispatching one extra small batch before the count catches up. This never risks
            // exceeding MaxCapacity (still capped below) and self-corrects on the very next
            // evaluation once the served caller's own decrement has run - at worst, a few
            // more items than strictly necessary get created, which a later scale-down naturally
            // reabsorbs once idle.
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

        // Fábrica (ADR001V03): dispatches a scale-up's bounded-concurrent factory batch onto the
        // thread pool instead of awaiting it inline, so the engine's single consumer thread stays
        // free to process other commands (a floor-guard replenishment, a heartbeat-driven
        // ReplaceOne, ...) while the batch is in flight. _currentCapacity is still mutated only on
        // the engine thread - later, when the batch's own FactoryBatchCompleted command is processed
        // - preserving the Orquestrador's sole-owner guarantee. Used only by Switch (manual),
        // EvaluateBacklogReactive (backlog), and EvaluateFloorGuard (floor) scale-up requests;
        // Warmup's own scale-up still calls MoveToCapacityAsync directly, unchanged - see its own
        // remarks for why. Tick's scale-down uses DispatchScaleDown below, the Remoção counterpart.
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
                    // O7-residual (Round 6, Resiliência): CreateItemsAsync's own throw path (zero
                    // items created, every attempt failed for a real reason) skips the tuple return
                    // entirely - always a genuine failure at that point (CreateItemsAsync only ever
                    // throws its own lastFailure, kept free of ordinary cancellation, R15).
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
                // its own listener-disposal point) before this batch finishes - see the
                // ScaleUp_*RacedByDisposeAsync observability tests, and DisposeAsync's own await on
                // _factoryBatchTask, which is what bounds that race.
                var scaledUp = created == quantity;
                var cancelledByShutdown = !scaledUp && _lifetime.IsCancellationRequested && !hadGenuineFailure;
                var statusOk = scaledUp || cancelledByShutdown;
                activity?.SetStatus(statusOk ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
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
                    // capacity itself is moot at this point (the buffer is shutting down).
                    completion?.TrySetResult(false);
                }
            });
        }

        // Remoção (ADR001V03): dispatches a scale-down's disposal of already-idle items onto the
        // thread pool instead of awaiting it inline, so the engine's single consumer thread stays
        // free to process other commands while the batch is in flight - the same isolation
        // DispatchScaleUp already gives Fábrica, for the same underlying reason
        // (Dispose()/DisposeAsync() on a real connection can block on I/O just as a factory call
        // can). Per the ADR's own wording, the Orquestrador dequeues here, on the engine thread,
        // BEFORE handing off to the background task - Remoção itself never touches the channel.
        // TryRead is fast/non-blocking (never awaits) regardless of how many items are actually
        // idle right now (opportunistic, partial-if-needed, same "keep whatever progress was made"
        // spirit already applied to scale-up) - only the disposal that follows can ever block, and
        // that is exactly what gets isolated. _currentCapacity is still mutated only on the engine
        // thread - later, when the batch's own RemovalBatchCompleted command is processed - per the
        // ADR's "confirmed completion, never the request" rule, same as Fábrica's own capacity
        // increment. Used by Switch (manual) and Tick's Monitor-driven (auto) scale-down requests.
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
                // Scale-down is different in kind from scale-up, not just degree (Round 7,
                // shutdown-vs-genuine-failure sweep, preserved from MoveToCapacityAsync's own
                // retired scale-down branch): the dequeue above never observes any token and
                // disposal never throws (per-item failures are already swallowed by
                // DisposeItemsDefensivelyAsync, F19) - a scale-down can only ever fully succeed or
                // partially succeed ("not enough idle items were available right now," entirely by
                // design). There is no cancellation path and no genuine-failure path to distinguish
                // here at all, so `!scaledDown` must never be reported as ActivityStatusCode.Error -
                // it would misrepresent this normal, expected outcome as a fault. "cancelled" is
                // still always emitted (false) to preserve the existing tag contract every scale
                // operation carries, not just the ones where it can actually be true.
                var scaledDown = removed.Count == quantity;
                // Round 1 (Estabilidade, v6 pre-release audit, confirmed by 2 independent
                // instances): a manual (pinned) scale-down that only partially completes (not
                // enough idle items were available right now) has nothing retrying it while the
                // pin is active - the Monitor is the only signal that would otherwise finish the
                // reduction as retained items become idle again, and it stays suppressed for the
                // whole pin duration. Accepted as known behavior (not fixed): the pool is never
                // corrupted (CurrentCapacity stays truthful, invariants hold) and it self-corrects
                // once the pin expires and ordinary Monitor ticks resume - but until then it is
                // silent otherwise, so surface it here. Not relevant for an "auto"-triggered
                // (Monitor-driven) scale-down: that path re-evaluates on every subsequent tick on
                // its own, with no pin ever suppressing it.
                if (!scaledDown && scaleTrigger == "manual")
                {
                    LogWarning($"ScaleDown to {target} only partially completed ({removed.Count}/{quantity} items removed) while a manual pin is active - the remaining reduction will not be retried until the pin expires.");
                }
                activity?.SetStatus(ActivityStatusCode.Ok);
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
                    // capacity itself is moot at this point (the buffer is shutting down).
                    completion?.TrySetResult(scaledDown);
                }
            });
        }

        // Monitor (ADR001V03's lowest-priority signal; ADR003V03's algorithm): a sliding-window
        // percentile + safety buffer "fair level", adjusted by a linear-regression trend projected
        // a configurable horizon ahead, clamped to [MinCapacity, MaxCapacity] - replaces the old
        // median-of-idle-samples algorithm (AutoScaleDecision), which was scale-down only.
        //
        // Demand is (in-use items) + (currently-waiting callers) - CurrentCapacity minus idle, plus
        // _waitingCount - never idle alone, which is clamped at zero and therefore blind to unmet
        // demand (see AutoScaleMonitor's own remarks on why an idle-derived proxy would silently
        // flatten the regression trend under sustained saturation).
        //
        // While demand keeps pace with or exceeds capacity ("active"), the window is paused
        // entirely and cleared the instant that episode ends - see _monitorActive's own remarks for
        // why this specific check is only reachable in a narrow case (pinned at MaxCapacity with
        // backlog), not the general "reactive episode" the validated simulation modeled, and why a
        // genuinely steady period (no scale op at all, deadband absorbing every small drift) still
        // lets the window grow to SamplesCount before demand next moves - a real, accepted
        // trade-off of the shipped defaults, not something this field or the pre-existing
        // per-scale-op clears (MoveToCapacityAsync/FactoryBatchCompleted/RemovalBatchCompleted)
        // prevent.
        //
        // Otherwise, this tick's demand joins the sliding window (bounded to SamplesCount,
        // evaluated every tick once at least 2 samples exist - not collected into one fixed batch
        // like the old median algorithm was) and the resulting target is compared against
        // CurrentCapacity: a change smaller than MonitorDeadband is ignored (measured to cut
        // oscillation under flat-but-noisy demand from 48 reversals in 300 ticks to 1); a larger
        // increase or decrease dispatches a background batch (DispatchScaleUp/DispatchScaleDown,
        // same as backlog/floor/manual) - this method has no async work of its own left once
        // Remoção moved scale-down's own execution off the engine thread too.
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
                // Round 1 (Observabilidade, v6 pre-release audit): the Monitor's own tick decision
                // otherwise left no trace at all when it decided NOT to scale - the scale.*
                // telemetry only exists for a dispatched operation, and this method had no logging
                // of its own. LogMessage itself now gates on IsEnabled(Debug) (Round 2 fix - it
                // didn't before), so this costs nothing when the caller hasn't opted into
                // Debug-level logs, despite running roughly every tick for the buffer's whole
                // lifetime rather than only per scale operation.
                LogMessage($"Monitor tick: demand={demand}, target={target} equals current capacity {CurrentCapacity} - no scale.");
                return;
            }
            // Reachability cap (same principle as the old median algorithm's R18/R19 threshold
            // cap, applied here to this algorithm's own deadband instead): MonitorDeadband must
            // never exceed the maximum delta actually reachable in the direction target is asking
            // for, or a small Capacity/MinCapacity/MaxCapacity span (e.g. ElasticCapacity(4, 2, 8),
            // span 2 < the default deadband of 3) would make that boundary mathematically
            // unreachable via this gate forever, however idle or saturated the buffer becomes.
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

        // Warmup's only remaining use of this method (Remoção, ADR001V03): once Switch's and
        // Tick's scale-down paths moved to DispatchScaleDown, and both their scale-up paths had
        // already moved to DispatchScaleUp, this method's sole surviving caller is Warmup - always
        // a scale-up (CurrentCapacity starts at 0, Capacity is always >= 2), always with no
        // scaleTrigger (the initial fill is not a "scale operation" the scale.* metrics describe,
        // see the class remarks). The scale-down branch, the telemetry block, and the
        // genuine-failure bookkeeping that only ever fed that (now-dead-by-construction, scaleTrigger
        // is never passed) telemetry were removed with it - not simplified defensively, since
        // nothing can reach them anymore.
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
                // Discard whatever samples (if any) accumulated before/during this scale (F6) -
                // they describe the pre-scale capacity, not the one the buffer has now. The next
                // scale-down decision must be based on a fresh window sampled after this point.
                _samples.Clear();
            }
        }

        // Fábrica (ADR001V03): waits out the current backoff window (if any) before a factory
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
            // Fábrica (ADR001V03): bounded concurrent creation, not one attempt at a time - up to
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
            // FactoryTimeout-bounded attempt), not with the sampling cadence (SamplesBase) - a
            // fixed, sampling-derived deadline could be smaller than quantity * FactoryTimeout for
            // any delta/FactoryTimeout combination, making a routine scale-up structurally
            // impossible regardless of the factory's actual health. Deliberately not tightened to
            // account for MaxConcurrentFactoryCalls - a looser bound that scales with sequential
            // worst case is always safe (never fires prematurely); it does not need to be the
            // tightest possible one.
            //
            // Armed lazily (once, by whichever attempt clears backoff first) rather than up front:
            // `overall`'s own wall-clock timer runs regardless of what token a wait is bound to, so
            // arming it up front would still let time spent waiting out an elevated backoff streak
            // (ApplyFactoryBackoffAsync below, deliberately not bound to `overall`) eat into this
            // deadline before Factory is ever called even once - the same self-inflicted failure
            // the paragraph above already guards against, just via the wall clock instead of the
            // token (advisor review).
            var deadlineArmed = 0;
            void ArmDeadline()
            {
                if (hasTimeout && Interlocked.CompareExchange(ref deadlineArmed, 1, 0) == 0)
                {
                    overall.CancelAfter(TimeSpan.FromTicks(FactoryTimeout.Ticks * quantity));
                }
            }
            using var gate = new SemaphoreSlim(MaxConcurrentFactoryCalls, MaxConcurrentFactoryCalls);

            // One attempt per requested item (R14) - a single item's timeout/exception no longer
            // abandons the whole batch by default. MaxConsecutiveFactoryFailures (default 0) still
            // gives up on the remaining not-yet-started items once a real streak of failures
            // happens, resetting on any success. Under real concurrency "consecutive" no longer has
            // an exact, ordered meaning (this is a single shared counter, not per-lane) - the same
            // "simple, not a circuit-breaker" approximation the ADR calls for, and identical to the
            // original sequential behavior whenever MaxConcurrentFactoryCalls is 1.
            //
            // Known, deliberately kept trade-off between these two defaults (revisited 2026-08-23,
            // no change made): all `quantity` attempts below are launched at once, so the first
            // MaxConcurrentFactoryCalls (default 4) of them always acquire a gate slot and run to
            // real completion before any of them can report a failure and set giveUp - a fully
            // broken factory therefore still gets up to 4 genuine attempts per batch, not 1, despite
            // MaxConsecutiveFactoryFailures's default of 0 ("give up on the first failure"). Already
            // covered by this method's own XML doc on maxConsecutiveFactoryFailures and exercised by
            // ScaleUp_WithElevatedBackoffStreak_StillGetsARealAttempt_WithinItsOwnTightDeadline's own
            // "first wave always runs" test. Considered and rejected: lowering
            // MaxConcurrentFactoryCalls's default to 1 would restore an exact "N consecutive"
            // guarantee, but defeats the whole reason Fábrica is concurrent by default (serializes
            // every ordinary healthy-factory batch, not just the pathological broken-factory case);
            // raising MaxConsecutiveFactoryFailures's default would make the number honest but moves
            // the wrong direction - more tolerance means MORE wasted attempts against a broken
            // factory, not fewer. Kept as-is: the real cost is bounded (wall-clock stays ~1x
            // FactoryTimeout regardless, since the wave is concurrent, not sequential) and already
            // documented; no default change addresses it without a worse trade-off elsewhere.
            async Task AttemptAsync()
            {
                lock (stateLock) { if (giveUp) return; }
                // Backoff happens before acquiring a concurrency slot, not while holding one - a
                // backed-off attempt should not tie up a permit other, not-yet-throttled attempts
                // could otherwise use. Bounded by `token` (shutdown), deliberately NOT `overall`
                // (the batch's own quantity * FactoryTimeout deadline): a long-elevated streak
                // (backoff capped at FactoryBackoffMax, e.g. 5s) could otherwise consume the whole
                // deadline before Factory is ever called even once, self-inflicting exactly the
                // "routine scale-up structurally impossible" failure the deadline formula above
                // exists to prevent - a healthy-but-currently-backed-off factory must still always
                // get a real attempt, not silently never reach one.
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
                        // Round 8 (Resiliência Achado 1): factoryTimeout is linked FROM overall, so
                        // it fires on everything overall does, plus the per-item deadline - passing
                        // it here instead loses no cancellation signal, but actually stops a
                        // well-behaved factory once .WaitAsync gives up on it, instead of leaving it
                        // running orphaned.
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
                // The overall deadline fired before every item could even be attempted once - but
                // that is not the only way to get here: a normal DisposeAsync racing this call also
                // cancels the very same linked token (R15). Only log a timeout when the deadline
                // itself actually elapsed; a caller-token/lifetime cancellation is an ordinary
                // shutdown, not evidence the factory is unhealthy.
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
            // N1/N2 (Round 7, Estabilidade): a pooled item's own Dispose()/DisposeAsync() had no
            // bound anywhere - unlike Factory (FactoryTimeout) and the heartbeat callback
            // (PulseHeartBeat, F16). Both callers of this method depend on it never hanging:
            // DisposeAsync()'s own drain loop (N1, a hang there just delays/blocks shutdown
            // itself) and, at the time N2 was found, a scale-down's own removal - which ran
            // inline on the single-consumer engine's own thread back then, so a hang there
            // stalled every other command until this bound gave up on it. Remoção (ADR001V03)
            // has since moved that call onto DispatchScaleDown's own background task, so this
            // bound no longer protects the engine thread for that caller specifically - it now
            // only bounds how long that background batch itself waits before giving up on a
            // still-hanging item. PulseHeartBeat is reused as the grace period here too, same
            // "can't cancel external code, so stop waiting instead" precedent F16 already
            // established for the heartbeat case.
            //
            // Round 8 (Estabilidade F29 + Observabilidade): two gaps in the fix above. (1) a plain
            // synchronous IDisposable.Dispose() blocks inline before any await point exists for
            // WaitAsync to bound - Task.Run below pushes it onto a thread-pool thread first, same
            // as the existing Task.Run(() => BufferHeartBeat?.Invoke(...)) pattern, so the grace
            // period actually applies to it too. (2) the previous sequential foreach cost N x
            // PulseHeartBeat for N hung items - Task.WhenAll bounds the whole batch by roughly one
            // PulseHeartBeat instead.
            var disposals = new List<Task>();
            foreach (var item in items)
            {
                disposals.Add(DisposeOneItemDefensivelyAsync(item));
            }
            await Task.WhenAll(disposals).ConfigureAwait(false);
        }

        private async Task DisposeOneItemDefensivelyAsync(T item)
        {
            // DisposeItemAsync(item) never actually gets cancelled by the timeout (there is no way
            // to force that on arbitrary user code) - it keeps running in the background, observed
            // by nothing further, but it can never fault an unobserved exception either: any
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
            // Fábrica (ADR001V03): same shared, cross-call backoff as CreateItemsAsync - a factory
            // that keeps failing every time it is asked for a replacement is throttled, not
            // hammered on every single Invalidate()/heartbeat-triggered cycle. This currently runs
            // inline on the engine thread (ProcessCommandAsync's ReplaceOne case), so - until
            // Fábrica's execution is decoupled from it (a later increment of this same ADR) - a
            // long backoff here also delays every other command behind it in the queue. Accepted
            // for now because the alternative (no backoff at all) is exactly the hammering this
            // mechanism exists to prevent; decoupling is what actually resolves the tension.
            await ApplyFactoryBackoffAsync(_lifetime.Token).ConfigureAwait(false);

            using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            factoryTimeout.CancelAfter(FactoryTimeout);
            var replaced = false;
            try
            {
                // Round 8 (Resiliência Achado 1): factoryTimeout is linked FROM _lifetime, so it
                // fires on everything _lifetime does, plus the per-item deadline - passing it here
                // instead loses no cancellation signal, but actually stops a well-behaved factory
                // once .WaitAsync gives up on it, instead of leaving it running orphaned.
                var item = await Factory(factoryTimeout.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                replaced = true;
                Interlocked.Exchange(ref _factoryFailureStreak, 0);
            }
            // Same distinction as CreateItemsAsync (R15): a normal DisposeAsync racing this
            // replacement cancels the same _lifetime token the per-item timeout is linked from -
            // that is an ordinary shutdown, not evidence the factory is unhealthy.
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                LogMessage("Replacement cancelled by shutdown.");
            }
            // R25 (Round 7, Resiliência): only the per-item factoryTimeout's own deadline actually
            // elapsing counts as a genuine timeout worth fabricating a TimeoutException for - a raw
            // OperationCanceledException/TaskCanceledException thrown by the factory itself (its own
            // unrelated internal timeout, e.g. HttpClient/gRPC/a DB driver) is not that, and logging a
            // fabricated TimeoutException for it discards the real exception entirely.
            catch (OperationCanceledException) when (factoryTimeout.IsCancellationRequested)
            {
                LogError(new TimeoutException("Timeout factory (replacement)"));
                Interlocked.Increment(ref _factoryFailureStreak);
            }
            catch (Exception ex)
            {
                // A non-cancellation factory failure must not escape and kill the engine loop; there is
                // no caller waiting on a replacement, so logging is the only outcome needed here. This
                // also now catches a factory-thrown OperationCanceledException that matched neither
                // guard above (R25) - logging the real exception instead of a fabricated one.
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
                    // ADR007V03: the callback now receives the raw T and returns a bool instead of
                    // the whole RingBufferValue<T> (removing the "do not dispose this yourself" trap
                    // by construction - there's no disposable object to misuse anymore). false is
                    // routed through the exact same Invalidate()/DisposeAsync() path any consumer's
                    // own Invalidate() call already goes through - one mechanism, not two. Defaults
                    // to healthy (true) if BufferHeartBeat is somehow null here, which should not
                    // happen given the startup gate above.
                    var heartbeatWork = Task.Run(() => BufferHeartBeat is null || BufferHeartBeat(acquired.Current));
                    try
                    {
                        var healthy = await heartbeatWork.WaitAsync(pulseTimeout.Token).ConfigureAwait(false);
                        if (!healthy)
                        {
                            acquired.Invalidate();
                        }
                        // Round 1 (Resiliência, v6 pre-release audit): unlike TurnbackAsync's general
                        // contract for an external caller's own DisposeAsync() call (a hang there is
                        // genuinely local to them - their own await, their own problem), "the caller"
                        // here is this pump itself. An unbounded await would let a single hanging
                        // Dispose() (on an item this same pump just invalidated) stall _heartbeatTask
                        // forever - and DisposeAsync() awaits _heartbeatTask via Task.WhenAll(pending)
                        // BEFORE its own cleanup (draining _availableItems, disposing _lifetime/
                        // _meter/_activitySource) runs, so the whole manager's shutdown would never
                        // complete, and _disposeGuard (already set) would make a retry a permanent
                        // silent no-op. Bounded the same way the F12/F15 timeout branch below already
                        // bounds a stuck callback's own dispose: PulseHeartBeat as grace period,
                        // deferred into _pendingHeartbeatDisposals (drained by DisposeAsync's own
                        // cleanup) if it doesn't finish in time. TurnbackAsync already posts
                        // ReplaceOne before this dispose even starts, so capacity is corrected either
                        // way, regardless of how long the underlying Dispose() actually takes.
                        var disposeTask = acquired.DisposeAsync().AsTask();
                        try
                        {
                            await disposeTask.WaitAsync(PulseHeartBeat).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            // Round 2 (Estabilidade, v6 pre-release audit): "capacity was already
                            // corrected" only holds for the Invalidate (unhealthy) path - dropped
                            // from the message rather than asserting something not always true;
                            // the reader doesn't need that detail to know the dispose is deferred.
                            LogWarning("Heart Beat item dispose did not complete within one pulse - deferring.");
                            // Round 2 (Resiliência, v6 pre-release audit): the raw disposeTask was
                            // deferred directly before this fix, unlike the F12/F15 branch below
                            // (which wraps its own deferred dispose in a ContinueWith that observes
                            // and logs a fault) - a late failure from this specific dispose reached
                            // neither LogError/OnError nor TaskScheduler.UnobservedTaskException,
                            // unlike every sibling deferred-dispose path in this file. Same fix:
                            // observe the fault via a continuation, and defer that (not the raw
                            // task) so DisposeAsync's own drain still waits for the real work too.
                            var observedDispose = disposeTask.ContinueWith(t =>
                            {
                                if (t.IsFaulted) LogError(t.Exception!.GetBaseException());
                            }, TaskScheduler.Default);
                            var stillPendingDispose = new List<Task>();
                            while (_pendingHeartbeatDisposals.TryTake(out var previousDispose))
                            {
                                if (!previousDispose.IsCompleted) stillPendingDispose.Add(previousDispose);
                            }
                            foreach (var previousDispose in stillPendingDispose) _pendingHeartbeatDisposals.Add(previousDispose);
                            _pendingHeartbeatDisposals.Add(observedDispose);
                        }
                    }
                    catch (OperationCanceledException) when (!heartbeatWork.IsCompleted)
                    {
                        // The callback is still running - either it blocked past its own pulse
                        // budget (F12), or an ordinary shutdown cancelled _lifetime while the
                        // callback was still going (F15). Either way it keeps running on its own
                        // thread-pool thread - a blocking synchronous callback cannot be forcibly
                        // cancelled, so it may still be reading/writing the resource right now.
                        // Two separate concerns, handled on two different timelines: replace the slot
                        // right away (via ReplaceOne directly, bypassing TurnbackAsync's combined
                        // dispose-then-replace) so capacity is not lost while the callback runs - same
                        // guarantee as before - but defer actually disposing the stuck resource until
                        // the orphaned callback truly finishes; disposing it now, while the callback
                        // might still be touching it, would be a use-after-dispose race on the
                        // caller's own object (a DB connection, a RabbitMQ channel). Its eventual
                        // outcome is still observed so a late fault cannot surface as an unobserved
                        // task exception.
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
                        // does, not merely when the async lambda is first scheduled - needed for
                        // DisposeAsync to be able to wait on real completion (below), not just on
                        // "was this continuation started" (Round 4, Estabilidade).
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
                        // would grow this bag for as long as the buffer runs, not just for as long
                        // as disposals are genuinely still in flight (Round 5, Estabilidade). Safe
                        // to compact here without losing anything mid-air: this loop is the bag's
                        // only writer (see the field's own comment), so nothing else can be adding
                        // while this take/re-add sequence runs.
                        var stillPending = new List<Task>();
                        while (_pendingHeartbeatDisposals.TryTake(out var previousDispose))
                        {
                            if (!previousDispose.IsCompleted) stillPending.Add(previousDispose);
                        }
                        foreach (var previousDispose in stillPending) _pendingHeartbeatDisposals.Add(previousDispose);
                        _pendingHeartbeatDisposals.Add(deferredDispose);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        // heartbeatWork.IsCompleted was already true by the time this exception was
                        // observed (the sibling catch's guard above did not match), so the callback
                        // is not still touching the resource - disposing now is safe. But the
                        // exception itself is still just an ordinary shutdown ending the wait, not a
                        // genuine failure - a residual instance of the same shutdown-vs-failure
                        // ambiguity R15/F15/R17/O1 already fix elsewhere, found while verifying this
                        // catch's own guard (Round 4, Estabilidade).
                        LogMessage("Heart Beat cancelled by shutdown after the callback had already finished.");
                        await acquired.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                        await acquired.DisposeAsync().ConfigureAwait(false);
                    }
                    LogMessage("Stopped Heart Beat item");
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
            catch (ObjectDisposedException)
            {
                // Same "manager disposed" shutdown as the OperationCanceledException case above -
                // there is a narrow window where DisposeAsync has already set _disposed but
                // _lifetime.Token has not yet observed cancellation; a heartbeat tick's own
                // internal acquire in that window throws this instead (Round 4, Estabilidade).
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
                    // Skip enqueueing while a scale operation is in progress (F6): the engine is a
                    // single serial consumer, so a Tick written now would just queue up behind the
                    // in-flight scale and get processed the instant it frees up - a burst of
                    // near-duplicate post-scale samples, not a time-spread window. _scaling is
                    // read here (its only reader) instead of in ProcessTick, where it was
                    // always already false by the time a queued Tick got processed.
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
            // Round 2 (Complexidade, v6 pre-release audit): the IsEnabled(Debug) check is
            // necessary here, not just cosmetic - without it, every call still built the
            // interpolated string and a closure even when Debug logging was off, and ProcessTick
            // now calls this roughly every SamplesBase/SamplesCount interval (300ms by default)
            // for the whole lifetime of every elastic buffer, not just per scale operation.
            // RingBufferBuilder's own LogMessage already has this guard; this one didn't.
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

        // A user-supplied Logger/ErrorHandler is untrusted external code (Round 6, F23 - found
        // independently by both the Estabilidade and Observabilidade passes): if it throws, that
        // must never be allowed to permanently break the heartbeat pump/leak a pooled item/make
        // DisposeAsync() itself throw, or otherwise escape into an unrelated core operation like
        // WarmupAsync/AcquireAsync. There is nothing further to log about the failure - the sink
        // itself is what's broken - so this is a silent best-effort swallow, extending the same
        // "must never throw regardless of how a background pump ended" philosophy DisposeAsync's
        // own comment already states.
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

        // Round 2 (Estabilidade, v6 pre-release audit): same F23 rationale as SafeInvokeSink above
        // - a user-supplied Logger is untrusted external code, and IsEnabled itself can throw
        // (Microsoft.Extensions.Logging's composite Logger aggregates and rethrows provider
        // exceptions, e.g. a provider disposed ahead of this manager during host shutdown).
        // LogMessage's own IsEnabled(Debug) check ran unguarded before this fix - unlike every
        // other call into Logger/ErrorHandler in this class - and could permanently kill
        // _engineTask or _heartbeatTask (ProcessTick/RunHeartbeatAsync have no catch broad enough
        // to survive it). Treat a throwing IsEnabled as "not enabled" and skip this one Debug line
        // rather than propagate.
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
