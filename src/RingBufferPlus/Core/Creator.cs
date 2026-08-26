// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace RingBufferPlus.Core
{
    internal sealed class Creator<T>
    {
        // Creator (ADR001V03): a simple growing backoff after consecutive genuine factory
        // failures - self-protection against hammering a broken factory, not a circuit breaker.
        // Persisted on Creator, not scoped to one CreateItemsAsync/CreateSingleReplacementAsync
        // call, so a factory that stays broken across several separate attempts (repeated
        // floor-guard cycles, repeated backlog-reactive requests, etc.) gets throttled
        // progressively over time - that cross-call repetition, not one batch's own bounded
        // concurrency, is what this guards against. Reset to zero by any genuine success,
        // incremented by any genuine (non-cancellation) failure. Not exposed as builder
        // configuration - "simple", per the ADR, means a small fixed policy, not a new tunable.
        private int _factoryFailureStreak;
        private static readonly TimeSpan FactoryBackoffBase = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan FactoryBackoffMax = TimeSpan.FromSeconds(5);

        public void ResetFailureStreak() => Interlocked.Exchange(ref _factoryFailureStreak, 0);

        public void RecordFailure() => Interlocked.Increment(ref _factoryFailureStreak);

        // Creator (ADR001V03): waits out the current backoff window (if any) before a factory
        // attempt starts. A zero streak (the common, healthy case) returns immediately - no delay,
        // no allocation. Exponential, capped at FactoryBackoffMax so a long-broken factory does not
        // grow the wait unboundedly: 1st failure -> FactoryBackoffBase, 2nd -> x2, 3rd -> x4, ...
        public async Task ApplyFactoryBackoffAsync(CancellationToken token)
        {
            var streak = Volatile.Read(ref _factoryFailureStreak);
            if (streak <= 0) return;

            var multiplier = Math.Pow(2, streak - 1);
            var ticks = Math.Min(FactoryBackoffBase.Ticks * multiplier, FactoryBackoffMax.Ticks);
            await Task.Delay(TimeSpan.FromTicks((long)ticks), token).ConfigureAwait(false);
        }

        public async Task<(int Created, bool HadGenuineFailure)> CreateItemsAsync(Func<CancellationToken, Task<T>> factory, TimeSpan factoryTimeout, int maxConcurrentFactoryCalls, byte maxConsecutiveFactoryFailures, int quantity, bool hasTimeout, ChannelWriter<T> writer, Action<string> logMessage, Action<Exception> logError, CancellationToken token)
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
                    overall.CancelAfter(TimeSpan.FromTicks(factoryTimeout.Ticks * quantity));
                }
            }
            using var gate = new SemaphoreSlim(maxConcurrentFactoryCalls, maxConcurrentFactoryCalls);

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
            // default of 0 ("give up on the first failure") - already covered by
            // IRingBufferBuilder{T}.Factory's own XML doc on maxConsecutiveFactoryFailures. Two
            // alternatives were considered and
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
                    using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    attemptTimeout.CancelAfter(factoryTimeout);
                    try
                    {
                        // attemptTimeout is linked from overall, so it fires on everything overall
                        // does, plus the per-item deadline. Passing it here loses no cancellation
                        // signal, but actually stops a well-behaved factory once .WaitAsync gives
                        // up on it, instead of leaving it running orphaned.
                        var item = await factory(attemptTimeout.Token).WaitAsync(attemptTimeout.Token).ConfigureAwait(false);
                        created.Add(item);
                        lock (stateLock) { consecutiveFailures = 0; }
                        Interlocked.Exchange(ref _factoryFailureStreak, 0);
                    }
                    catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested && !overall.IsCancellationRequested)
                    {
                        var timeout = new TimeoutException("Timeout factory");
                        logError(timeout);
                        lock (stateLock)
                        {
                            lastFailure = timeout;
                            if (++consecutiveFailures > maxConsecutiveFactoryFailures) giveUp = true;
                        }
                        Interlocked.Increment(ref _factoryFailureStreak);
                    }
                    catch (Exception ex) when (!overall.IsCancellationRequested)
                    {
                        logError(ex);
                        lock (stateLock)
                        {
                            lastFailure = ex;
                            if (++consecutiveFailures > maxConsecutiveFactoryFailures) giveUp = true;
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
                    logMessage($"ScaleUp cancelled by shutdown, {created.Count}/{quantity} item(s) already created.");
                }
                else
                {
                    logError(new TimeoutException($"Timeout ScaleUp {created.Count}/{quantity} - keeping the {created.Count} item(s) already created."));
                }
            }

            foreach (var item in created)
            {
                await writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
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
    }
}
