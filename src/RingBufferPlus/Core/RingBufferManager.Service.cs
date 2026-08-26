// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed partial class RingBufferManager<T>
    {
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
                await Removal.DisposeItemsDefensivelyAsync(remainingItems, PulseHeartBeat, LogWarning, LogError).ConfigureAwait(false);

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
                    await Removal.DisposeItemAsync(value.Current).ConfigureAwait(false);
                }
            }
            catch (ChannelClosedException)
            {
                // The manager was disposed concurrently with the turnback; the item can no longer
                // be returned to the pool, so dispose it instead of leaking it.
                await Removal.DisposeItemAsync(value.Current).ConfigureAwait(false);
            }
        }
    }
}
