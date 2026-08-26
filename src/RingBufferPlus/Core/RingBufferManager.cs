// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

// Design note (ADR001V03/ADR005): all mutable scale state (_currentCapacity, fault counters,
// samples) belongs exclusively to the single consumer loop (RunEngineAsync). No other thread
// mutates it, so no lock or semaphore is needed. Callers only post commands into an unbounded
// Channel<EngineCommand> and, optionally, await a completion signal.
//
// Two deliberate simplifications versus v4, both authorized by ADR006V01 (no compatibility
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
    internal sealed partial class RingBufferManager<T> : IRingBufferManualScaleService<T>
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
        // failure catch blocks and the scale.* tags are cold paths with no measured benefit.
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
        // item dispose (see the timeout branches in RunHeartbeatAsync) - added only from
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

        private readonly Creator<T> _creator = new();

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
            // AcquireCoreAsync.
            _meter.CreateObservableGauge("ringbufferplus.capacity.current",
                () => new Measurement<int>(CurrentCapacity, new KeyValuePair<string, object?>("buffer.name", Name)),
                description: "Current capacity of the buffer.");

            _engineTask = Task.Run(RunEngineAsync);
        }
    }
}
