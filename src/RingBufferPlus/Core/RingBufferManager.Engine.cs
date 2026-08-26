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
        // active for an elastic pool, gated only by Elastic.
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
                    (created, hadGenuineFailure) = await _creator.CreateItemsAsync(Factory, FactoryTimeout, MaxConcurrentFactoryCalls, MaxConsecutiveFactoryFailures, quantity, hasTimeout: true, _availableItems.Writer, LogMessage, LogError, _lifetime.Token).ConfigureAwait(false);
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
                    await Removal.DisposeItemsDefensivelyAsync(removed, PulseHeartBeat, LogWarning, LogError).ConfigureAwait(false);
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

        // Warmup is now this method's only caller, since Switch's and Tick's scale-down paths
        // moved to DispatchScaleDown (Removal) and their scale-up paths moved to DispatchScaleUp
        // (Creator), both ADR001V03. So this is always a scale-up (CurrentCapacity starts at 0,
        // Capacity is always >= 2), always with no scaleTrigger (the initial fill is not a "scale
        // operation" the scale.* metrics describe, see the class remarks). The scale-down branch,
        // the telemetry block, and the genuine-failure bookkeeping that only ever fed that
        // now-unreachable telemetry were removed along with it, not just simplified - nothing can
        // reach them anymore.
        private async Task<bool> MoveToCapacityAsync(int target, bool hasTimeout, CancellationToken token)
        {
            var current = CurrentCapacity;
            if (target == current) return true;

            _scaling = true;
            try
            {
                var quantity = target - current;
                LogMessage($"Starting ScaleUp {quantity}.");
                var (created, _) = await _creator.CreateItemsAsync(Factory, FactoryTimeout, MaxConcurrentFactoryCalls, MaxConsecutiveFactoryFailures, quantity, hasTimeout, _availableItems.Writer, LogMessage, LogError, token).ConfigureAwait(false);
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
            await _creator.ApplyFactoryBackoffAsync(_lifetime.Token).ConfigureAwait(false);

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
                _creator.ResetFailureStreak();
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
                _creator.RecordFailure();
            }
            catch (Exception ex)
            {
                // A non-cancellation factory failure must not escape and kill the engine loop; no
                // caller is waiting on a replacement, so logging is the only outcome needed here.
                // This also catches a factory-thrown OperationCanceledException that matched
                // neither catch above, logging the real exception instead of a fabricated one.
                LogError(ex);
                _creator.RecordFailure();
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
