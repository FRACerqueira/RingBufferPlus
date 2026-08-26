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
                        // DisposeAsync can wait on real completion, not just on whether
                        // this continuation started.
                        var deferredDispose = heartbeatWork.ContinueWith(async t =>
                        {
                            if (t.IsFaulted) LogError(t.Exception!.GetBaseException());
                            try
                            {
                                await Removal.DisposeItemAsync(acquired.Current).ConfigureAwait(false);
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
    }
}
