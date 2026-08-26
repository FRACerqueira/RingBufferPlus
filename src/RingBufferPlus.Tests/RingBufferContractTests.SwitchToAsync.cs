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

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ReturnsFalse_WhenAlreadyAtRequestedTarget()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractNoOpSwitch", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(2, 10, 5, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();
            Assert.True(service.IsInitCapacity);

            // Act: already at InitCapacity, asking to switch to InitCapacity again must be a no-op.
            var result = await service.SwitchToAsync(ScaleSwitch.InitCapacity, TimeSpan.FromMinutes(1));

            // Assert
            Assert.False(result);

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithoutLockWhenScaling_ReturnsImmediately_AndScalesInBackground()
        {
            // Arrange: LockWhenScaling is NOT set (defaults to false) -> background, non-blocking scaling.
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBackgroundSwitch", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(2, 6, 3, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            // Act
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            // Assert: the request is accepted immediately...
            Assert.True(accepted);

            // ...and eventually takes effect in the background.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.3 - At most one concurrent SwitchToAsync caller wins the same scale request
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ConcurrentSwitchToAsync_AtMostOneCallerEnqueuesTheScaleRequest()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractConflictingScale", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(2, 12, 4, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            // Act: many concurrent callers race to request the same scale target. Because the engine
            // is a single sequential consumer, exactly one caller wins - a deterministic guarantee,
            // not a race reproduction.
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)))));

            // Assert: exactly one caller wins the race and gets the request accepted.
            Assert.Equal(1, results.Count(accepted => accepted));

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.5 - The internal scale mechanism rejects work after disposal
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ThrowsObjectDisposed_AfterDisposeAsync()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposedSwitch", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(2, 8, 4)
                .Build();
            await service.WarmupAsync();
            await service.DisposeAsync();

            // Act & Assert: calling SwitchToAsync after disposal must throw, not silently no-op.
            // There's no separate scale queue anymore - the whole engine loop is gone - but the
            // public contract must still reject work submitted after disposal.
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)));
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenFactoryThrowsDuringScaleUp_PropagatesRealException_AndEngineSurvives()
        {
            // Arrange: factory is healthy for warmup (capacity 2), then always throws for the scale-up.
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFactoryThrowsDuringScale", null);
            var service = builder
                .Factory(_ => throwing ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;

            // Act: the scale-up's factory calls throw - the engine must survive this, and every
            // call below must still complete rather than hang.
            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            var completed = await Task.WhenAny(switchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(switchTask, completed);
            var switchEx = await Assert.ThrowsAsync<InvalidOperationException>(() => switchTask);
            Assert.Equal("factory down", switchEx.Message);

            // Assert: capacity did not move, and the engine is still alive for further work.
            Assert.True(service.IsInitCapacity);
            throwing = false;
            var acquireTask = service.AcquireAsync().AsTask();
            var acquireCompleted = await Task.WhenAny(acquireTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(acquireTask, acquireCompleted);
            Assert.True((await acquireTask).Successful);

            var disposeTask = service.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(disposeTask, disposeCompleted);
            await disposeTask;
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenFactoryThrowsOperationCanceledExceptionDuringScaleUp_PropagatesRealException_NotASilentFalse()
        {
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFactoryThrowsOceDuringScale", null);
            var service = builder
                .Factory(_ => throwing ? throw new TaskCanceledException("factory's own unrelated timeout") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            var completed = await Task.WhenAny(switchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(switchTask, completed);
            var switchEx = await Assert.ThrowsAsync<TaskCanceledException>(() => switchTask);
            Assert.Equal("factory's own unrelated timeout", switchEx.Message);

            Assert.True(service.IsInitCapacity);
            throwing = false;
            var acquireTask = service.AcquireAsync().AsTask();
            var acquireCompleted = await Task.WhenAny(acquireTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(acquireTask, acquireCompleted);
            Assert.True((await acquireTask).Successful);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // On the unlocked SwitchToAsync path (LockWhenScaling not set), the caller never awaits
        // completion.Task: `!LockWhenScaling || await completion.Task...` short-circuits before the
        // right side ever runs. If the engine later fails the scale-up and resolves that TCS via
        // TrySetException, nothing observes the fault. Once the TCS is garbage-collected, that fault
        // surfaces as an unobserved task exception.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenUnlockedAndTheScaleUpFails_DoesNotSurfaceAnUnobservedTaskException()
        {
            var scaleUpShouldThrow = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractUnlockedSwitchUnobservedException", null);
            var service = await builder
                .Factory(_ => scaleUpShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .OnError(_ => { })
                .ElasticCapacity(2, 4, 2, 1, TimeSpan.FromSeconds(5))
                .BuildWarmupAsync();

            scaleUpShouldThrow = true;

            var unobserved = new List<Exception>();
            void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
            {
                lock (unobserved) unobserved.Add(e.Exception.GetBaseException());
                e.SetObserved();
            }
            TaskScheduler.UnobservedTaskException += Handler;
            try
            {
                // Deliberately NOT using LockWhenScaling(): the unlocked path is exactly what's
                // under test - it returns true immediately without waiting for the eventual outcome.
                var switched = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
                Assert.True(switched);

                // Give the engine loop time to actually process the Switch command and fault
                // `completion` before forcing collection.
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (service.IsInitCapacity && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Handler;
            }

            lock (unobserved)
            {
                Assert.DoesNotContain(unobserved, e => e is InvalidOperationException ioe && ioe.Message == "factory down");
            }

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.14 - The scale-up deadline must scale with the work requested (quantity * FactoryTimeout),
        // not with the sampling cadence (SamplesBase) - and a scale-up that still can't finish in
        // time must keep whatever capacity it already gained instead of discarding it.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ScaleUpNeedingMoreTimeThanSamplesBase_StillSucceeds()
        {
            // 3 items at ~150ms each (~450ms total) would not fit in a 300ms SamplesBase-derived
            // deadline, but comfortably fits quantity(3) * FactoryTimeout(1s) = 3s.
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleUpNeedsMoreTimeThanSamplesBase", null);
            var service = builder
                .Factory(async _ => { await Task.Delay(150); return 1; }, TimeSpan.FromSeconds(1))
                .ElasticCapacity(2, 5, 2, 1, TimeSpan.FromMilliseconds(300))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            Assert.True(moved);
            Assert.True(service.IsMaxCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenFactoryFailsPartwayThroughScaleUp_KeepsPartialCapacity()
        {
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPartialScaleUp", null);
            var service = builder
                .Factory(_ =>
                {
                    // Calls 1-2 are the warmup (Capacity=2). The scale-up to 5 needs 3 more calls
                    // (3, 4, 5); let 3 and 4 succeed and 5 fail, so 2 of the 3 requested items are
                    // actually created before the failure.
                    var call = Interlocked.Increment(ref callCount);
                    if (call >= 5)
                    {
                        throw new InvalidOperationException("factory down");
                    }
                    return Task.FromResult(call);
                })
                .ElasticCapacity(2, 5, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Partial progress is not surfaced as an exception - only "nothing at all was gained" is.
            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            Assert.False(moved);

            // 2 of the 3 requested items were created before the 3rd call failed - capacity must
            // reflect that partial gain (4), not fall back to the pre-scale value (2).
            Assert.Equal(4, service.CurrentCapacity);

            // The 2 gained items must be real and usable, not just counted.
            var acquired = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                var value = await service.AcquireAsync();
                Assert.True(value.Successful);
                acquired.Add(value);
            }
            foreach (var value in acquired)
            {
                await value.DisposeAsync();
            }

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.23 - maxConsecutiveFactoryFailures: the default (0) must keep fail-fast behavior (a
        // single item's failure still gives up on the rest of the batch); opting in to a higher
        // value must let the batch keep trying the remaining not-yet-attempted items instead.
        //
        // Creator creates items with bounded concurrency (ADR001V03, MaxConcurrentFactoryCalls,
        // default 4), so "give up on the remaining items" only stops items still queued behind that
        // concurrency window - anything already launched keeps running regardless.
        // maxConcurrentFactoryCalls: 1 below pins this test to the original one-at-a-time shape, so
        // it isolates the tolerance behavior from the concurrency behavior; the next test covers the
        // bounded-concurrency case explicitly.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithDefaultFailureTolerance_StillAbandonsTheBatchOnTheFirstFailure()
        {
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDefaultToleranceIsFailFast", null);
            var service = builder
                .Factory(async _ =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    if (call == 4)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    return call;
                }, TimeSpan.FromMilliseconds(500)) // maxConsecutiveFactoryFailures defaults to 0.
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            // Default tolerance (0) preserves the original behavior: only the 1 item attempted
            // before the failure (call 3) is kept - calls 5 and 6 are never even attempted.
            Assert.False(moved);
            Assert.Equal(3, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // ADR001V03: the bounded-concurrency counterpart to the test above. With
        // maxConcurrentFactoryCalls: 2 and 8 items requested, giving up only stops a later wave
        // (still queued behind the concurrency window) from starting - it can't un-start an attempt
        // already in flight. This is a bound, not an exact count: the shared give-up flag can race a
        // sibling attempt's own success, so one or two stragglers beyond the running wave may still
        // start before the flag is visible to them (the same deliberately simple, non-circuit-breaker
        // behavior ADR001V03 accepts). What must hold regardless is the actual guarantee: nowhere
        // near the full batch of 8 is ever attempted.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithBoundedConcurrency_GivesUpWellBeforeAttemptingTheFullBatch()
        {
            var totalCalls = 0;
            var claimed = 0;
            var scalingUp = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBoundedConcurrencyFailFast", null);
            var service = builder
                .Factory(async _ =>
                {
                    // Only the scale-up (not the warmup fill) exercises the claim-and-fail logic
                    // below - both share this factory, but warmup must always succeed cleanly.
                    if (!Volatile.Read(ref scalingUp))
                    {
                        return 0;
                    }
                    Interlocked.Increment(ref totalCalls);
                    // Both first-wave attempts must genuinely be in flight together before either
                    // resolves - a real factory call takes real time; this yield is what makes that
                    // true here instead of leaving it to incidental scheduling.
                    await Task.Delay(50);
                    if (Interlocked.CompareExchange(ref claimed, 1, 0) == 0)
                    {
                        throw new InvalidOperationException("factory down");
                    }
                    return 1;
                })
                .ElasticCapacity(2, 10, 2, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 2)
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();
            scalingUp = true;

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            Assert.False(moved);
            // The first wave (2 concurrent attempts) always runs; at most a small, bounded number
            // of stragglers beyond it can also start before give-up is visible to them (see the
            // comment above) - but nowhere near the full 8 requested items are ever attempted.
            Assert.True(totalCalls <= 4, $"Expected give-up to stop well short of the full batch of 8, but {totalCalls} items were attempted.");
            Assert.True(service.CurrentCapacity < 10, $"Expected a partial gain, not the full requested capacity (10); got {service.CurrentCapacity}.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // ADR001V03: Creator's batch runs on the thread pool instead of blocking the engine's
        // single consumer thread (Orchestrator) inline, so a second command must be dequeued and
        // handled immediately, not queued up behind the whole in-flight batch. A second, overlapping
        // scale-up request is still correctly rejected (only one batch at a time, to avoid a
        // MaxCapacity overshoot), but that rejection itself must be prompt - proving the engine loop
        // was free to look at it right away.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ScaleUp_DoesNotBlockTheEngineLoop_WhileTheBatchIsInFlight()
        {
            var scalingUp = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleUpDoesNotBlockEngine", null);
            var service = builder
                .Factory(async _ =>
                {
                    // Only the scale-up (not the warmup fill) takes the long path - warmup must
                    // always complete quickly and cleanly.
                    if (Volatile.Read(ref scalingUp))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    return 0;
                })
                .ElasticCapacity(2, 4, 2, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
                .Build();
            await service.WarmupAsync();
            scalingUp = true;

            // Dispatches a scale-up whose single factory call takes ~2s. No LockWhenScaling here:
            // SwitchToAsync only awaits Accepted, returning as soon as the batch is dispatched -
            // not when it finishes.
            var firstAccepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            Assert.True(firstAccepted);

            // Act: immediately issue a second Switch request while the first batch is still
            // running in the background. Before this decoupling, the engine's single consumer
            // thread would still be blocked awaiting the first batch inline, so this call would
            // not even be looked at until the ~2s factory delay elapsed. With Creator decoupled,
            // the engine is free to dequeue and reject it immediately (_scaling is true).
            var sw = Stopwatch.StartNew();
            var secondAccepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            sw.Stop();

            Assert.False(secondAccepted);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
                $"Expected the engine to reject the second request promptly instead of blocking behind the in-flight batch; took {sw.Elapsed}.");

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithFailureToleranceOptedIn_StillAttemptsTheRemainingItems()
        {
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractOneBadItemDoesNotAbortBatch", null);
            var service = builder
                .Factory(async _ =>
                {
                    // Calls 1-2 are the warmup (Capacity=2). The scale-up to 6 needs 4 more calls
                    // (3, 4, 5, 6); call 4 (the 2nd scale-up item) times out, the other 3 succeed
                    // fast - with tolerance opted in, the batch must still end up with 3 of the 4
                    // requested items, not just the 1 that happened to be attempted before the
                    // failure.
                    var call = Interlocked.Increment(ref callCount);
                    if (call == 4)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    return call;
                }, TimeSpan.FromMilliseconds(500), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            // The batch did not fully complete (call 4 never made it in), but 3 of the 4 requested
            // items (calls 3, 5, 6) must still have been created - not just the 1 attempted before
            // the mid-batch failure.
            Assert.False(moved);
            Assert.Equal(5, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Manual pin (ADR007V03): SwitchToAsync substitutes for the Monitor's own predictive
        // output for a required, explicit duration - it never suppresses the floor guard or the
        // backlog-reactive signal, which keep acting on their own independent triggers regardless.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhilePinIsActive_SuppressesTheMonitorsOwnScaleDown()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPinSuppressesMonitor", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 10, 2, 3, TimeSpan.FromMilliseconds(300))
                .BuildWarmupAsync();

            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMilliseconds(900));
            Assert.True(accepted);
            // SwitchToAsync itself only waits for the dispatch to be ACCEPTED, not for the
            // background scale-up batch to actually finish - poll instead of asserting immediately.
            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(2);
            while (service.CurrentCapacity < 10 && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(10);
            }
            Assert.Equal(10, service.CurrentCapacity);

            // All 10 items sit idle with nothing ever acquiring them - with a 100ms tick interval
            // (300ms baseTimer / 3 samples), the Monitor's own near-zero demand window would
            // already have dispatched a scale-down toward MinCapacity well within this delay if
            // the pin above were not suppressing it.
            await Task.Delay(500);
            Assert.Equal(10, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_AfterPinExpires_MonitorResumesAndScalesDown()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPinExpiresMonitorResumes", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 10, 2, 3, TimeSpan.FromMilliseconds(300))
                .BuildWarmupAsync();

            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMilliseconds(400));
            Assert.True(accepted);
            // SwitchToAsync itself only waits for the dispatch to be ACCEPTED, not for the
            // background scale-up batch to actually finish - poll instead of asserting immediately.
            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(2);
            while (service.CurrentCapacity < 10 && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(10);
            }
            Assert.Equal(10, service.CurrentCapacity);

            // Past the pin's own duration, the Monitor needs at least two more tick intervals
            // (100ms each) before it can act: the sliding window was emptied when the pin's own
            // scale-up batch completed and stays empty for the pin's entire duration (Tick is
            // skipped outright while pinned), so the first tick after expiry only adds the first
            // sample back (ProcessTick's own "fewer than 2 samples" early return) - only the tick
            // after that has enough to compute a target and dispatch.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity == 10 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.True(service.CurrentCapacity < 10, $"Expected the Monitor to resume and scale back down once the pin expired. Actual: {service.CurrentCapacity}.");

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithNonPositivePinDuration_ThrowsArgumentOutOfRange()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPinDurationValidation", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 6, 2)
                .Build();
            await service.WarmupAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.Zero));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(-1)));

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Investigated and REFUTED a suspected logging gap - kept as a permanent regression guard,
        // not because a bug was found.
        //
        // The suspicion: a manual SwitchToAsync with LockWhenScaling=false never awaits
        // completion.Task, so a failure resolves via TrySetException instead of the "nobody is
        // waiting, LogError it" branch that floor/backlog/auto-triggered scales use. The unlocked
        // path's own fault-observing continuation doesn't call LogError itself, so reading only
        // that path suggested the documented promise ("any genuine factory failure gets LogError'd
        // with the real exception") silently didn't hold here.
        //
        // What that reading missed: CreateItemsAsync's own per-attempt catch already calls
        // LogError(ex) for every individual failed attempt, unconditionally - before any
        // batch-level aggregation happens, and regardless of which signal triggered the scale-up or
        // who's waiting on it. An early probe seemed to confirm the suspicion (1 logged error
        // instead of the 4 expected from 4 concurrent attempts), but that was an artifact of the
        // probe's factory throwing synchronously - letting the first attempt finish and set giveUp
        // before the other three ever called Factory. A genuinely async factory (below) reproduces
        // all 4 real attempts, each independently logged.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_UnlockedManualScaleUp_GenuineFactoryFailure_StillLogsTheError()
        {
            var loggedErrors = new List<Exception>();
            var callCount = 0;
            var manager = new RingBufferManager<int>(CancellationToken.None)
            {
                Name = "ContractSwitchUnlockedLog",
                Capacity = 2,
                MinCapacity = 2,
                MaxCapacity = 6,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(30),
                SamplesBase = TimeSpan.FromSeconds(30),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(300),
                Elastic = true,
                LockWhenScaling = false,
                ErrorHandler = ex => { lock (loggedErrors) loggedErrors.Add(ex); },
                Factory = async _ =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    // The initial warmup fill (Capacity=2) succeeds; every later call - the scale-up
                    // this test triggers - fails for real. Genuinely async (like a real RabbitMQ/DB
                    // client), not a synchronous throw - a synchronous throw would let the first of
                    // the concurrent attempts complete (and set giveUp) before the others ever call
                    // Factory at all, undercounting how many attempts genuinely run.
                    if (n <= 2) return n;
                    await Task.Delay(10);
                    throw new InvalidOperationException("boom");
                }
            };
            await manager.WarmupAsync();

            var accepted = await manager.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(5));
            Assert.True(accepted); // LockWhenScaling=false returns as soon as the engine accepts it

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (loggedErrors.Count < 4 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            // All MaxConcurrentFactoryCalls (default 4) concurrent attempts genuinely ran and each
            // independently reached LogError - confirmed count, not just non-empty, since the whole
            // point of this test is that the real number matters (see the class comment in
            // RingBufferContractTests.cs).
            Assert.Equal(4, loggedErrors.Count);
            Assert.All(loggedErrors, ex => Assert.True(ex is InvalidOperationException ioe && ioe.Message == "boom"));

            await manager.DisposeAsync();
        }
    }
}
