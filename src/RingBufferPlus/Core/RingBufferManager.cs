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
        private readonly Channel<LogMessageBackground> _logQueue = Channel.CreateUnbounded<LogMessageBackground>();
        private Lazy<Task> _warmup;
        private readonly Task _engineTask;
        private readonly List<int> _samples = [];

        private readonly Meter _meter = new("RingBufferPlus");
        private readonly ActivitySource _activitySource = new("RingBufferPlus");
        private readonly Histogram<double> _acquireDuration;
        private readonly Counter<long> _acquireFaults;
        private readonly Counter<long> _scaleOperations;
        private readonly Histogram<double> _scaleDuration;

        private Task? _heartbeatTask;
        private Task? _sampleTickTask;
        private Task? _loggerTask;

        // Deferred dispose continuations from an orphaned heartbeat callback (F12/F15) - added
        // only from RunHeartbeatAsync's own loop, so by the time _heartbeatTask (awaited in
        // DisposeAsync before this bag is snapshotted) has completed, no more entries can arrive.
        private readonly ConcurrentBag<Task> _pendingHeartbeatDisposals = new();

        private bool _disposed;
        private int _disposeGuard;
        private int _currentCapacity;
        private volatile bool _scaling;
        private int _faultCount;

        #endregion

        #region configuration (set by RingBufferBuilder via object initializer)

        public required string Name { get; init; }

        public int Capacity { get; init; }

        public int MinCapacity { get; init; }

        public int MaxCapacity { get; init; }

        public TimeSpan FactoryTimeout { get; init; }

        public byte MaxConsecutiveFactoryFailures { get; init; }

        public TimeSpan PulseHeartBeat { get; init; }

        public TimeSpan SamplesBase { get; init; }

        public int SamplesCount { get; init; }

        public bool AutoScaleFault { get; init; }

        public byte NumberFault { get; init; }

        public TimeSpan AcquireTimeout { get; init; }

        public bool LockWhenScaling { get; init; }

        /// <summary>
        /// True only for elastic buffers without autoscale-on-fault. Guards the escaped-cast path:
        /// <see cref="SwitchToAsync(ScaleSwitch)"/> is not exposed at the type level otherwise (ADR007),
        /// but a caller that casts back to <see cref="IRingBufferManualScaleService{T}"/> must not silently no-op.
        /// </summary>
        public bool ManualSwitchAllowed { get; init; }

        public ILogger? Logger { get; init; }

        public bool BackgroundLogger { get; init; }

        public Action<ILogger?, Exception>? ErrorHandler { get; init; }

        public Action<RingBufferValue<T>>? BufferHeartBeat { get; init; }

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

            _acquireDuration = _meter.CreateHistogram<double>("ringbufferplus.acquire.duration", unit: "s", description: "Duration of AcquireAsync calls, in seconds.");
            _acquireFaults = _meter.CreateCounter<long>("ringbufferplus.acquire.faults", description: "Count of AcquireAsync calls that timed out with no item available.");
            _scaleOperations = _meter.CreateCounter<long>("ringbufferplus.scale.operations", description: "Count of scale-up/scale-down operations, tagged by direction, trigger, and success.");
            _scaleDuration = _meter.CreateHistogram<double>("ringbufferplus.scale.duration", unit: "s", description: "Duration of scale-up/scale-down operations, in seconds, tagged by direction and success.");
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

        private async ValueTask<RingBufferValue<T>> AcquireCoreAsync(bool countsTowardFaultBudget, CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureWarmupAsync().ConfigureAwait(false);

            using var activity = _activitySource.StartActivity("RingBufferPlus.Acquire");
            activity?.SetTag("buffer.name", Name);

            var sw = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource(AcquireTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _lifetime.Token, cancellation);
            try
            {
                var item = await _availableItems.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                _acquireDuration.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("buffer.name", Name),
                    new KeyValuePair<string, object?>("acquire.success", true));
                activity?.SetTag("success", true);
                activity?.SetTag("timed_out", false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new RingBufferValue<T>(Name, sw.Elapsed, true, item, TurnbackAsync);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                var timedOut = timeoutCts.IsCancellationRequested;
                if (timedOut)
                {
                    LogWarning("RingBuffer without resource");
                    if (AutoScaleFault && countsTowardFaultBudget)
                    {
                        _commands.Writer.TryWrite(EngineCommand.Fault());
                    }
                    _acquireFaults.Add(1, new KeyValuePair<string, object?>("buffer.name", Name));
                }
                // acquire.timed_out mirrors the activity's own "timed_out" tag (Round 5,
                // Observabilidade - finding O6): without it, this histogram's failed rows can't be
                // told apart from an ordinary shutdown/caller-cancellation, same gap O1/O2 already
                // closed on the metrics/activity side of Scale/Acquire elsewhere.
                _acquireDuration.Record(sw.Elapsed.TotalSeconds,
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
                return new RingBufferValue<T>(Name, sw.Elapsed, false, default!, null);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The caller's own token fired, not a timeout/disposal - this rethrows unchanged
                // (see the sibling catch above), but the activity/duration must still record an
                // outcome before it does, or a trace shows an outcome-less span for this call.
                _acquireDuration.Record(sw.Elapsed.TotalSeconds,
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
        }

        public async Task<bool> SwitchToAsync(ScaleSwitch value)
        {
            if (!ManualSwitchAllowed)
            {
                throw new InvalidOperationException("Manual scale switching is not available: the buffer has a fixed capacity, or autoscale-on-fault is enabled (see ADR007).");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                await EnsureWarmupAsync().ConfigureAwait(false);

                var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Switch(value, accepted, completion), _lifetime.Token).ConfigureAwait(false);

                // Both bounded by _lifetime.Token: if this command loses its race against disposal
                // and is abandoned unread in the channel, this must not hang forever waiting for
                // signals nobody will ever send.
                var wasAccepted = await accepted.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                if (!wasAccepted)
                {
                    return false;
                }
                return !LockWhenScaling || await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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
                // _logQueue is intentionally NOT completed here - DisposeAsync itself still has
                // log-generating work ahead (the WhenAll failure catch below, the deferred-
                // heartbeat-disposal block, and the item-drain loop in the finally all call
                // LogMessage/LogError). Completing the queue this early silently drops every one
                // of those under BackgroundLogger=true, since LogMessage/LogError only ever
                // TryWrite to it in that mode, with no synchronous fallback (Round 5, Estabilidade,
                // Finding B). It is completed, and _loggerTask awaited, at the very end of this
                // method instead - after every possible log call above has already run.

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
                // _loggerTask is deliberately NOT included here - it can only finish once
                // _logQueue is completed, which is deferred to the end of this method (see above).
                // Awaiting it here, before that completion, would hang forever.

                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    //ignore: expected once _lifetime is cancelled
                }
                catch (Exception ex)
                {
                    // Disposal must be best-effort and never throw regardless of how a background
                    // pump ended, but an unexpected fault here is still worth surfacing through the
                    // configured logger/ErrorHandler rather than being fully silent.
                    LogError(ex);
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
                        LogWarning($"DisposeAsync did not wait for {deferredDisposals.Length} orphaned heartbeat callback(s) still running past the grace period - their resource(s) will be disposed once/if the callback(s) finish, but not before this DisposeAsync() call returned.");
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
                // exception trigger; DisposeItemsDefensivelyAsync (already used by RemoveItemsAsync)
                // is the same fix applied to this trigger.
                await DisposeItemsDefensivelyAsync(remainingItems).ConfigureAwait(false);

                _lifetime.Dispose();
                _meter.Dispose();
                _activitySource.Dispose();

                // Only now, after every DisposeAsync-generated log call above has already run, is
                // it safe to complete the queue and let the logger pump finish draining it (Finding B).
                _logQueue.Writer.TryComplete();
                if (_loggerTask is not null)
                {
                    try
                    {
                        await _loggerTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        //ignore: expected once _lifetime is cancelled
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                    }
                }
            }
        }

        private Task EnsureWarmupAsync() => Volatile.Read(ref _warmup).Value;

        private async Task WarmupCoreAsync()
        {
            // _loggerTask is guarded by "is null", not just "!_disposed", because a retried warmup
            // attempt (ADR011) re-enters this method - without the guard, a retry after a failed
            // first attempt would start a second logger pump and orphan the first one (never
            // awaited again, since _loggerTask would be overwritten).
            if (_loggerTask is null && !_disposed && BackgroundLogger && (Logger is not null || ErrorHandler is not null))
            {
                _loggerTask = Task.Run(RunLoggerAsync);
            }

            LogMessage("Starting warmup process.");

            bool reached;
            try
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Warmup(completion), _lifetime.Token).ConfigureAwait(false);
                // Bounded by _lifetime.Token: if this command loses its race against disposal and is
                // abandoned unread in the channel, this must not hang forever waiting for a completion
                // signal nobody will ever send.
                reached = await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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
            if (!_disposed && AutoScaleFault)
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
                    await DisposeItemAsync(value.Current).ConfigureAwait(false);
                    _commands.Writer.TryWrite(EngineCommand.ReplaceOne());
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
                    try
                    {
                        await ProcessCommandAsync(cmd).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                    }
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
                    if (target == CurrentCapacity)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                        break;
                    }
                    cmd.Accepted?.TrySetResult(true);
                    try
                    {
                        var moved = await MoveToCapacityAsync(target, hasTimeout: true, _lifetime.Token, scaleTrigger: "manual").ConfigureAwait(false);
                        cmd.Completion?.TrySetResult(moved);
                    }
                    catch (Exception ex)
                    {
                        cmd.Completion?.TrySetException(ex);
                    }
                    break;

                case EngineCommandKind.Fault:
                    _faultCount++;
                    // >= (not >): NumberFault's own doc says "after first fault" for its default of 1 -
                    // the sample RingBufferPlusBasicTriggerScale passes 0 specifically to get "fires on
                    // the first fault", which only holds if the comparison includes equality.
                    if (_faultCount < NumberFault)
                    {
                        break;
                    }
                    if (CurrentCapacity == MaxCapacity)
                    {
                        // F7: nothing to scale to right now, but forget this batch of faults anyway -
                        // otherwise the counter piles up unboundedly while pinned at MaxCapacity, and
                        // the very next fault after a later scale-down would immediately re-trigger a
                        // scale back to MaxCapacity instead of requiring a fresh batch.
                        _faultCount = 0;
                        break;
                    }
                    // Must always be strictly greater than CurrentCapacity - a plain equality
                    // check against MinCapacity picks Capacity even when Capacity == MinCapacity
                    // (a legal configuration), making this a no-op (target == current) forever.
                    var next = CurrentCapacity < Capacity ? Capacity : MaxCapacity;
                    try
                    {
                        var moved = await MoveToCapacityAsync(next, hasTimeout: true, _lifetime.Token, scaleTrigger: "auto").ConfigureAwait(false);
                        // R7: only forget this batch of faults once the scale-up actually completed -
                        // a failed/partial attempt must not burn the whole budget, so the very next
                        // fault retries instead of requiring an entire fresh batch while already
                        // struggling.
                        if (moved)
                        {
                            _faultCount = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        // No caller is waiting on a Fault-triggered scale-up; logging is the only
                        // outcome needed, and the engine loop must keep running regardless.
                        LogError(ex);
                    }
                    break;

                case EngineCommandKind.ReplaceOne:
                    await CreateSingleReplacementAsync().ConfigureAwait(false);
                    break;

                case EngineCommandKind.Tick:
                    await ProcessTickAsync().ConfigureAwait(false);
                    break;
            }
        }

        private async Task ProcessTickAsync()
        {
            _samples.Add(_availableItems.Reader.Count);
            if (_samples.Count < SamplesCount)
            {
                return;
            }
            var median = AutoScaleDecision.Median(_samples);
            _samples.Clear();
            var target = AutoScaleDecision.EvaluateScaleDown(median, CurrentCapacity, MinCapacity, Capacity, AutoScaleFault);
            if (target.HasValue)
            {
                try
                {
                    await MoveToCapacityAsync(target.Value, hasTimeout: true, _lifetime.Token, scaleTrigger: "auto").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // No caller is waiting on a Tick-triggered scale-down; logging is the only outcome
                    // needed, and the engine loop must keep running regardless.
                    LogError(ex);
                }
            }
        }

        private int ResolveTarget(ScaleSwitch value) => value switch
        {
            ScaleSwitch.MinCapacity => MinCapacity,
            ScaleSwitch.MaxCapacity => MaxCapacity,
            _ => Capacity
        };

        private async Task<bool> MoveToCapacityAsync(int target, bool hasTimeout, CancellationToken token, string? scaleTrigger = null)
        {
            var current = CurrentCapacity;
            if (target == current) return true;

            var direction = target > current ? "up" : "down";
            using var activity = scaleTrigger is null ? null : _activitySource.StartActivity("RingBufferPlus.Scale");
            activity?.SetTag("buffer.name", Name);
            activity?.SetTag("direction", direction);
            activity?.SetTag("trigger", scaleTrigger);
            var sw = scaleTrigger is null ? null : Stopwatch.StartNew();

            _scaling = true;
            var ok = false;
            // Set only by the scale-up branch, when a genuine (non-cancellation) factory failure
            // happened somewhere in the batch even though it also made partial progress - see the
            // cancelledByShutdown computation below (Round 5, Observabilidade, finding O7).
            var hadGenuineFailure = false;
            try
            {
                if (target > current)
                {
                    var quantity = target - current;
                    LogMessage($"Starting ScaleUp {quantity}.");
                    var (created, batchHadGenuineFailure) = await CreateItemsAsync(quantity, hasTimeout, token).ConfigureAwait(false);
                    hadGenuineFailure = batchHadGenuineFailure;
                    LogMessage("End ScaleUp.");
                    ok = created == quantity;
                    // A partial scale-up still gained real, usable capacity - advance by however
                    // many items were actually created, not just on hitting the full target.
                    if (created > 0)
                    {
                        Volatile.Write(ref _currentCapacity, current + created);
                    }
                }
                else
                {
                    var quantity = current - target;
                    LogMessage($"Starting ScaleDown {quantity}.");
                    var removed = await RemoveItemsAsync(quantity).ConfigureAwait(false);
                    LogMessage("End ScaleDown.");
                    ok = removed == quantity;
                    // A partial scale-down still reduced real capacity (R6) - advance by however
                    // many items were actually removed, not just on hitting the full target. Same
                    // "keep whatever progress was made" spirit already applied to scale-up (R5/P1#8).
                    if (removed > 0)
                    {
                        Volatile.Write(ref _currentCapacity, current - removed);
                    }
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
                if (scaleTrigger is not null)
                {
                    // `ok` also reflects a scale attempt that threw (it stays false, set only on the
                    // success path above) - so a failed or timed-out operation is never recorded
                    // identically to a successful one. But `!ok` alone conflates a genuine factory
                    // failure/timeout with an ordinary DisposeAsync() racing this operation (the
                    // same distinction R15/F15/R17/R18 already make for logs - Round 4,
                    // Observabilidade, finding O1) - token is always _lifetime.Token for every
                    // caller of this method that passes a scaleTrigger, so this check is exactly
                    // that same "was this shutdown, not failure" test. `!hadGenuineFailure` closes
                    // a gap in that same test (Round 5, finding O7): a batch can make partial
                    // progress despite a real per-item failure, and then have its still-not-
                    // attempted items cancelled by an ordinary shutdown moments later - without this
                    // check, that real failure would be masked entirely, reported as "just a
                    // shutdown" with no trace it happened.
                    var cancelledByShutdown = !ok && token.IsCancellationRequested && !hadGenuineFailure;
                    activity?.SetStatus(ok || cancelledByShutdown ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                    activity?.SetTag("cancelled", cancelledByShutdown);
                    _scaleOperations.Add(1,
                        new KeyValuePair<string, object?>("buffer.name", Name),
                        new KeyValuePair<string, object?>("direction", direction),
                        new KeyValuePair<string, object?>("trigger", scaleTrigger),
                        new KeyValuePair<string, object?>("success", ok),
                        new KeyValuePair<string, object?>("cancelled", cancelledByShutdown));
                    _scaleDuration.Record(sw!.Elapsed.TotalSeconds,
                        new KeyValuePair<string, object?>("buffer.name", Name),
                        new KeyValuePair<string, object?>("direction", direction),
                        new KeyValuePair<string, object?>("success", ok),
                        new KeyValuePair<string, object?>("cancelled", cancelledByShutdown));
                }
            }
        }

        private async Task<(int Created, bool HadGenuineFailure)> CreateItemsAsync(int quantity, bool hasTimeout, CancellationToken token)
        {
            var created = new List<T>(quantity);
            Exception? lastFailure = null;
            var consecutiveFailures = 0;
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            // The deadline scales with the work actually requested (each item already has its own
            // FactoryTimeout-bounded attempt), not with the sampling cadence (SamplesBase) - a
            // fixed, sampling-derived deadline could be smaller than quantity * FactoryTimeout for
            // any delta/FactoryTimeout combination, making a routine scale-up structurally
            // impossible regardless of the factory's actual health.
            if (hasTimeout) overall.CancelAfter(TimeSpan.FromTicks(FactoryTimeout.Ticks * quantity));
            try
            {
                // One attempt per requested item (R14) - a single item's timeout/exception no
                // longer abandons the whole batch by default. Bounding by attempt count (not just
                // created.Count < quantity) is what keeps a systematically broken factory failing
                // fast instead of retrying the same slot for the entire overall deadline:
                // MaxConsecutiveFactoryFailures (default 0) still gives up on the remaining items
                // once a real streak of failures happens, resetting on any success so isolated
                // hiccups in an otherwise healthy batch don't count towards it.
                for (var attempt = 0; attempt < quantity; attempt++)
                {
                    using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    factoryTimeout.CancelAfter(FactoryTimeout);
                    try
                    {
                        var item = await Factory(overall.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                        created.Add(item);
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) when (factoryTimeout.IsCancellationRequested && !overall.IsCancellationRequested)
                    {
                        var timeout = new TimeoutException("Timeout factory");
                        LogError(timeout);
                        lastFailure = timeout;
                        if (++consecutiveFailures > MaxConsecutiveFactoryFailures) break;
                    }
                    catch (Exception ex) when (!overall.IsCancellationRequested)
                    {
                        LogError(ex);
                        lastFailure = ex;
                        if (++consecutiveFailures > MaxConsecutiveFactoryFailures) break;
                    }
                }
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
            if (created.Count == 0 && lastFailure is not null)
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
            foreach (var item in items)
            {
                try
                {
                    await DisposeItemAsync(item).ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    // One item's Dispose()/DisposeAsync() throwing must not stop the rest from being
                    // disposed, nor escape and kill the engine loop.
                    LogError(disposeEx);
                }
            }
        }

        private async Task<int> RemoveItemsAsync(int quantity)
        {
            var removed = new List<T>(quantity);
            // Opportunistic (R6): take only whatever is already idle right now via a
            // non-blocking TryRead loop - never wait for busy items to be returned. The engine is
            // a single serial consumer (ADR001), so blocking here to wait for capacity to free up
            // would stall every other command (Fault, another Switch, ReplaceOne, Tick) for as
            // long as that wait takes. A partial reduction is fine - MoveToCapacityAsync advances
            // _currentCapacity by whatever was actually removed either way.
            while (removed.Count < quantity && _availableItems.Reader.TryRead(out var item))
            {
                removed.Add(item);
            }
            if (removed.Count > 0)
            {
                await DisposeItemsDefensivelyAsync(removed).ConfigureAwait(false);
            }
            return removed.Count;
        }

        private async Task CreateSingleReplacementAsync()
        {
            using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            factoryTimeout.CancelAfter(FactoryTimeout);
            try
            {
                var item = await Factory(_lifetime.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Same distinction as CreateItemsAsync (R15): a normal DisposeAsync racing this
                // replacement cancels the same _lifetime token the per-item timeout is linked
                // from - that is an ordinary shutdown, not evidence the factory is unhealthy.
                if (_lifetime.IsCancellationRequested)
                {
                    LogMessage("Replacement cancelled by shutdown.");
                }
                else
                {
                    LogError(new TimeoutException("Timeout factory (replacement)"));
                }
            }
            catch (Exception ex)
            {
                // A non-cancellation factory failure must not escape and kill the engine loop; there is
                // no caller waiting on a replacement, so logging is the only outcome needed here.
                LogError(ex);
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
                    var heartbeatWork = Task.Run(() => BufferHeartBeat?.Invoke(acquired));
                    try
                    {
                        await heartbeatWork.WaitAsync(pulseTimeout.Token).ConfigureAwait(false);
                        await acquired.DisposeAsync().ConfigureAwait(false);
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
                    // read here (its only reader) instead of in ProcessTickAsync, where it was
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

        private async Task RunLoggerAsync()
        {
            try
            {
                await foreach (var item in _logQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(item.Message))
                    {
                        if (item.LogLevel == LogLevel.Debug)
                        {
                            logMessageForDbg(Logger!, Name, item.Message, null);
                        }
                        else if (item.LogLevel == LogLevel.Warning)
                        {
                            logMessageFoWrn(Logger!, Name, item.Message, null);
                        }
                    }
                    if (item.Error is not null)
                    {
                        if (ErrorHandler is null)
                        {
                            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {item.Error.Message} ";
                            logMessageForErr(Logger!, Name, msg, item.Error);
                        }
                        else
                        {
                            ErrorHandler.Invoke(Logger, item.Error);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //ignore
            }
        }

        #endregion

        #region logging

        private void LogMessage(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Debug, msg, null));
            }
            else
            {
                logMessageForDbg(Logger, Name, msg, null);
            }
        }

        private void LogWarning(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Warning, msg, null));
            }
            else
            {
                logMessageFoWrn(Logger, Name, msg, null);
            }
        }

        private void LogError(Exception error)
        {
            if (Logger is null && ErrorHandler is null) return;
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Error, null, error));
            }
            else if (ErrorHandler is null)
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {error.Message} ";
                logMessageForErr(Logger!, Name, msg, error);
            }
            else
            {
                ErrorHandler.Invoke(Logger, error);
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageFoWrn = LoggerMessage.Define<string, string>(LogLevel.Warning, 0, "RingBufferManager({Source}) : {Message}");

        #endregion

        private enum EngineCommandKind { Warmup, Switch, Fault, ReplaceOne, Tick }

        private sealed record EngineCommand
        {
            public required EngineCommandKind Kind { get; init; }
            public ScaleSwitch? Target { get; init; }
            public TaskCompletionSource<bool>? Accepted { get; init; }
            public TaskCompletionSource<bool>? Completion { get; init; }

            public static EngineCommand Warmup(TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Warmup, Completion = completion };

            public static EngineCommand Switch(ScaleSwitch target, TaskCompletionSource<bool> accepted, TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Switch, Target = target, Accepted = accepted, Completion = completion };

            public static EngineCommand Fault() => new() { Kind = EngineCommandKind.Fault };

            public static EngineCommand ReplaceOne() => new() { Kind = EngineCommandKind.ReplaceOne };

            public static EngineCommand Tick() => new() { Kind = EngineCommandKind.Tick };
        }
    }
}
