// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    internal static class Removal
    {
        public static async Task DisposeItemsDefensivelyAsync<T>(IEnumerable<T> items, TimeSpan pulseHeartBeat, Action<string> logWarning, Action<Exception> logError)
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
                disposals.Add(DisposeOneItemDefensivelyAsync(item, pulseHeartBeat, logWarning, logError));
            }
            await Task.WhenAll(disposals).ConfigureAwait(false);
        }

        private static async Task DisposeOneItemDefensivelyAsync<T>(T item, TimeSpan pulseHeartBeat, Action<string> logWarning, Action<Exception> logError)
        {
            // DisposeItemAsync(item) never actually gets cancelled by the timeout - there's no way
            // to force that on arbitrary user code. It keeps running in the background if the wait
            // below times out, but it can never fault an unobserved exception either: any
            // exception it eventually throws is still caught below, just later than this method
            // waited for.
            var disposeTask = Task.Run(() => DisposeItemAsync(item).AsTask());
            try
            {
                await disposeTask.WaitAsync(pulseHeartBeat).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logWarning("A pooled item's Dispose()/DisposeAsync() did not complete within the grace period - it will keep running in the background, but this call is no longer waiting for it.");
                _ = disposeTask.ContinueWith(t =>
                {
                    if (t.IsFaulted) logError(t.Exception!.GetBaseException());
                }, TaskScheduler.Default);
            }
            catch (Exception disposeEx)
            {
                // One item's Dispose()/DisposeAsync() throwing must not stop the rest from being
                // disposed, nor escape and kill the engine loop.
                logError(disposeEx);
            }
        }

        public static async ValueTask DisposeItemAsync<T>(T item)
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
    }
}
