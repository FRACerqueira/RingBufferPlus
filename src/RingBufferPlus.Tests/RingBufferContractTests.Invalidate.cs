// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public partial class RingBufferContractTests
    {

        // ---------------------------------------------------------------------
        // 1.4d - Invalidate() replaces the item via the engine, keeping capacity stable.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_ReplacesTheItem_AndSubsequentAcquireStillSucceeds()
        {
            // Arrange
            var factoryCalls = 0;
            var manager = CreateFixedManager(2, _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(1);
            });
            await manager.WarmupAsync();
            Assert.Equal(2, factoryCalls);

            var acquired = await manager.AcquireAsync();
            Assert.True(acquired.Successful);

            // Act: invalidate the acquired item instead of turning it back.
            acquired.Invalidate();
            await acquired.DisposeAsync();

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (factoryCalls < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            // Assert: exactly one replacement item was created (not zero, not a duplicate)...
            Assert.Equal(3, factoryCalls);

            // ...and capacity is still fully served afterwards.
            var second = await manager.AcquireAsync();
            Assert.True(second.Successful);

            await manager.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // v6.0.0 / ADR001V03: unlike its two siblings (DisposeAsync()'s drain loop,
        // RemoveItemsAsync via DisposeItemsDefensivelyAsync), TurnbackAsync's Invalidate() branch
        // Invalidate() used to await the old item's Dispose()/DisposeAsync() BEFORE enqueuing
        // EngineCommand.ReplaceOne() in a `finally`. If that Dispose() hangs forever, the `finally`
        // never runs, so the slot is never replaced and CurrentCapacity stays wrong forever.
        //
        // Fixed by enqueuing the replacement first, unconditionally, before awaiting the old item's
        // disposal. Pool-wide capacity must not depend on how long - or whether - that call ever
        // returns. A caller-owned item whose Dispose() hangs is that caller's own problem (their own
        // DisposeAsync() call on the RingBufferValue<T> hangs too); it shouldn't corrupt shared pool
        // state for everyone else. The blast radius here is local (one caller's own call blocks),
        // not global, so the fix is a direct reordering rather than another bounded defensive-dispose
        // wrapper.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenItemDisposeHangs_StillReplacesTheSlot_WithoutWaitingForIt()
        {
            using var release = new ManualResetEventSlim(false);
            var factoryCalls = 0;
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractInvalidateHangingDispose", null);
            var service = builder
                .Factory(_ =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return Task.FromResult(new HangingDisposeProbe(release));
                })
                .FixedCapacity(2)
                .Build();
            await service.WarmupAsync();
            Assert.Equal(2, factoryCalls);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            acquired.Invalidate();

            // Deliberately not awaited to completion: the old item's Dispose() is hanging (capped
            // at 10s by the probe itself so a broken fix can't hang the test process forever).
            var disposeTask = acquired.DisposeAsync().AsTask();
            try
            {
                // The replacement's factory call must happen promptly - well before the hang ever
                // resolves - because capacity truthfulness must not depend on the old item's own
                // Dispose() returning.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (Volatile.Read(ref factoryCalls) < 3 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
                Assert.Equal(3, Volatile.Read(ref factoryCalls));
            }
            finally
            {
                release.Set();
                await disposeTask;
                await service.DisposeAsync();
            }
        }

        // ---------------------------------------------------------------------
        // Companion to the test above. TryWrite(ReplaceOne()) now runs unconditionally as the first
        // step of the Invalidate() branch, even after the manager is already disposed. On an
        // already-completed channel, TryWrite returns false instead of throwing, so this must fall
        // through and dispose the item exactly once - the same as the non-Invalidate case,
        // TurnbackAsync_AfterManagerDisposed_DisposesTheItem_InsteadOfLeakingIt
        // (RingBufferContractTests.Dispose.cs).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_AfterManagerDisposed_StillDisposesTheItemExactlyOnce()
        {
            var probe = new DisposableProbe();
            var manager = new RingBufferManager<DisposableProbe>(default)
            {
                Name = "ContractInvalidateAfterDispose",
                Capacity = 2,
                MinCapacity = 2,
                MaxCapacity = 2,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(5),
                SamplesBase = TimeSpan.FromSeconds(5),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(300),
                Factory = _ => Task.FromResult(probe)
            };
            await manager.WarmupAsync();

            var held = await manager.AcquireAsync();
            held.Invalidate();
            await manager.DisposeAsync();

            // TryWrite(ReplaceOne()) against the already-completed _commands channel must not
            // throw; DisposeItemAsync below it must still run.
            var ex = await Record.ExceptionAsync(() => held.DisposeAsync().AsTask());
            Assert.Null(ex);

            // One for the still-idle item the drain loop disposed, one for `held` via this path -
            // never zero (leaked), never more than twice (double-disposed).
            Assert.Equal(2, probe.DisposeCount);
        }

        // ---------------------------------------------------------------------
        // 1.34 - Sweep for unguarded external-callback invocations: TurnbackAsync's
        // Invalidate() branch called DisposeItemAsync(value.Current) then
        // _commands.Writer.TryWrite(EngineCommand.ReplaceOne()) with no guard around the dispose
        // call - a user item type throwing from Dispose()/DisposeAsync() there skipped the
        // ReplaceOne enqueue entirely, permanently losing that pool slot. Same shape of bug as
        // elsewhere in this sweep: a later necessary step skipped because an earlier one, calling
        // into external/user code, threw uncaught. The exception itself is expected to still
        // propagate to the caller unchanged (no public contract change - this
        // one is fixed with a `finally`, not a swallow) - only the replacement bookkeeping must
        // not depend on that call succeeding.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenItemsDisposeThrows_StillQueuesAReplacement()
        {
            // v6.0.0's bounded-concurrent Creator (ADR001V03) creates warmup's 2 items concurrently
            // by default, so which physical factory call becomes "the one that gets acquired first"
            // is no longer deterministic - throwing-on-dispose is picked by identity, on the actual
            // acquired instance, instead of baked into the factory by call order.
            IRingBufferBuilder<ThrowingOnDisposeProbe> builder = new RingBufferBuilder<ThrowingOnDisposeProbe>("ContractInvalidateThrowingDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new ThrowingOnDisposeProbe(throwOnDispose: false)))
                .AcquireTimeout(TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            acquired.Current.ThrowOnDispose = true;
            acquired.Invalidate();
            await Assert.ThrowsAsync<InvalidOperationException>(() => acquired.DisposeAsync().AsTask());

            // The replacement must still have been queued despite the throw above - both slots
            // must be acquirable, proving the pool wasn't left permanently one item short.
            var first = await service.AcquireAsync();
            var second = await service.AcquireAsync();
            Assert.True(first.Successful, "Expected the untouched slot to still be acquirable.");
            Assert.True(second.Successful, "Expected the replacement item to be acquirable despite the throwing Dispose().");
            await first.DisposeAsync();
            await second.DisposeAsync();

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.41 - Invalidate() (and the heartbeat's stuck-item path) disposes
        // the old item BEFORE enqueuing ReplaceOne, so by the time CreateSingleReplacementAsync
        // runs, one real item is already gone from the pool. When its Factory call then fails, the
        // method must not only log - _currentCapacity must reflect the loss too, or
        // CurrentCapacity would report the old, too-high number forever, with no retry and no
        // correction.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenTheReplacementFactoryFails_DecrementsCurrentCapacity_InsteadOfReportingTheLostItem()
        {
            var errors = new List<Exception>();
            var replacementShouldThrow = false;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractReplacementFailureLosesCapacity", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.Equal(2, service.CurrentCapacity);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            replacementShouldThrow = true;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync

            // Stage 1: wait until the replacement attempt has actually run and failed, so a later
            // capacity mismatch cannot be confused with "the replacement never happened".
            var failureDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < failureDeadline)
            {
                lock (errors)
                {
                    if (errors.Any(e => e is InvalidOperationException ioe && ioe.Message == "factory down")) break;
                }
                await Task.Delay(20);
            }
            lock (errors)
            {
                Assert.Contains(errors, e => e is InvalidOperationException ioe && ioe.Message == "factory down");
            }

            // Stage 2: the old item is gone and no replacement arrived, so the pool really is one
            // item smaller - CurrentCapacity must say so.
            var capacityDeadline = DateTime.UtcNow.AddSeconds(2);
            while (service.CurrentCapacity == 2 && DateTime.UtcNow < capacityDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(1, service.CurrentCapacity);
            // The floor guard (ADR001V03) does retry this in the background
            // once CurrentCapacity(1) < MinCapacity(2) - but replacementShouldThrow never flips
            // back to false in this test, so every retry keeps failing and capacity never recovers.
            // See Invalidate_WhenTheReplacementFactoryFailsThenRecovers_FloorGuardRestoresMinCapacity
            // below for the self-healing case this test does not cover.

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.41b - Floor guard (ADR001V03): the gap the test above documents (a failed replacement
        // shrinks CurrentCapacity below MinCapacity with nothing that retries it) is now closed -
        // the Orchestrator keeps retrying, at the pace of Creator's own existing consecutive-failure
        // backoff, until the floor is restored or the buffer is disposed. This is the
        // highest-priority signal of all (floor guard > backlog-reactive > manual pin > Monitor).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenTheReplacementFactoryFailsThenRecovers_FloorGuardRestoresMinCapacity()
        {
            var replacementShouldThrow = false;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFloorGuardSelfHeals", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.Equal(2, service.CurrentCapacity);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            replacementShouldThrow = true;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync

            // Stage 1: wait until the replacement has actually failed and the floor breach is
            // observable, so recovery below cannot be confused with "it never actually broke".
            var breachDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity == 2 && DateTime.UtcNow < breachDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(1, service.CurrentCapacity);

            // Stage 2: the factory recovers, but no caller does anything - no AcquireAsync, no
            // WarmupAsync, no SwitchToAsync. Only the floor guard's own background retries (this
            // test's entire point) can restore MinCapacity from here.
            replacementShouldThrow = false;

            var healDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity < 2 && DateTime.UtcNow < healDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(2, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.41c - Floor guard (ADR001V03): the grace window ("the public 'below minimum' property
        // only becomes true if replenishment fails to restore MinCapacity within one FactoryTimeout
        // cycle") is a distinct claim from the self-healing test above - it needs a factory that
        // never recovers, so the window actually has a chance to elapse instead of the breach
        // resolving first. There is no public property yet (ADR007V03), so this asserts the
        // substitute this pass wires instead: an OnError report once the window elapses.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenTheReplacementFactoryStaysBroken_ReportsBelowMinimum_AfterOneFactoryTimeoutCycle()
        {
            var replacementShouldThrow = false;
            var errors = new List<Exception>();
            var factoryTimeout = TimeSpan.FromMilliseconds(150);

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFloorGuardGraceWindow", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1), factoryTimeout)
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.Equal(2, service.CurrentCapacity);

            var acquired = await service.AcquireAsync();
            replacementShouldThrow = true;
            var trigger = DateTime.UtcNow;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync

            // The factory never recovers here, so the guard's own retries keep failing forever -
            // giving the grace window (one FactoryTimeout cycle) a real chance to elapse.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (errors)
                {
                    if (errors.Any(e => e is InvalidOperationException ioe && ioe.Message.Contains("below minimum capacity"))) break;
                }
                await Task.Delay(20);
            }
            DateTime reportedAt;
            lock (errors)
            {
                Assert.Contains(errors, e => e is InvalidOperationException ioe && ioe.Message.Contains("below minimum capacity"));
                reportedAt = DateTime.UtcNow;
            }
            // Not instant: reported meaningfully later than the trigger, not on the very first
            // evaluation (which would mean the grace window was never actually applied). A
            // generous margin below the real 150ms window avoids flakiness from backoff/scheduling
            // jitter while still ruling out "reported on the first check".
            Assert.True(reportedAt - trigger >= TimeSpan.FromMilliseconds(100), $"Expected the report to wait out roughly one FactoryTimeout cycle ({factoryTimeout}), not fire near-instantly. Actual delay: {reportedAt - trigger}.");

            // Still genuinely below the floor while this is reported - the factory never recovered.
            Assert.Equal(1, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // EvaluateFloorGuard's LogError had no latch tied to the grace
        // window - each call that found the window already elapsed logged again, so a persistently
        // broken factory reports forever (throttled only by the shared factory-retry backoff, not
        // by the guard itself). Decided with the maintainer: log once when the grace window first
        // elapses, then keep re-alerting on the same cadence as a fallback so an ongoing outage
        // never goes fully silent - see FloorGuardDecisionTests for the precise, deterministic
        // latch behavior. This test only proves the end-to-end wiring: the report does repeat at
        // least once more given enough elapsed time, it does not go silent after the first one.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenTheReplacementFactoryStaysBroken_RepeatsBelowMinimumReport_AsAFallback()
        {
            var replacementShouldThrow = false;
            var errors = new List<Exception>();
            var factoryTimeout = TimeSpan.FromMilliseconds(150);

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFloorGuardRepeatsAsFallback", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1), factoryTimeout)
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var acquired = await service.AcquireAsync();
            replacementShouldThrow = true;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync, which never recovers

            var deadline = DateTime.UtcNow.AddSeconds(5);
            int reportCount;
            do
            {
                await Task.Delay(50);
                lock (errors)
                {
                    reportCount = errors.Count(e => e is InvalidOperationException ioe && ioe.Message.Contains("below minimum capacity"));
                }
            } while (reportCount < 2 && DateTime.UtcNow < deadline);

            await service.DisposeAsync();

            Assert.True(reportCount >= 2, $"Expected the report to fire again after the first one (fallback alerting for an ongoing outage must not go silent). Actual reports within 5s: {reportCount}.");
        }

        // ---------------------------------------------------------------------
        // 1.42-1.43 - Factory used to receive a token that never actually fires at the per-item
        // FactoryTimeout deadline. So a cooperative factory that honors CancellationToken was never
        // actually told to stop once the library gave up waiting on it - it kept running, orphaned,
        // and any eventual successful result was silently dropped without disposal. Passing the
        // per-item deadline's own token instead costs nothing and lets a well-behaved factory
        // actually stop. A factory that ignores cancellation entirely is unaffected by this fix and
        // remains a documented caller responsibility.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenTheReplacementFactoryTimesOut_ActuallyCancelsTheAbandonedFactoryCall()
        {
            var callCount = 0;
            var cancelledPromptly = new TaskCompletionSource<bool>();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractReplacementFactoryTokenPropagation", null);
            var service = await builder
                .Factory(async ct =>
                {
                    if (Interlocked.Increment(ref callCount) <= 2)
                    {
                        return 1; // the FixedCapacity(2) warmup fill
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelledPromptly.TrySetResult(true);
                        throw;
                    }
                    return 2;
                }, TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync (the slow 3rd factory call)

            var completed = await Task.WhenAny(cancelledPromptly.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(cancelledPromptly.Task, completed);

            await service.DisposeAsync();
        }
    }
}
