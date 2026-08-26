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
        // 1.2 - Cancellation mid scale-up must not corrupt manager state or leak an unhandled fault
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_CancelledMidOperation_DoesNotThrowUnhandled()
        {
            // Arrange: the factory blocks (observing its token) only after warmup, so the initial
            // capacity can be reached, and a subsequent scale-up can be cancelled mid-flight.
            var blockFactory = false;
            var gate = new SemaphoreSlim(0);
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractCancelScaleUp", null);
            var service = builder
                .Factory(async ct =>
                {
                    if (blockFactory)
                    {
                        await gate.WaitAsync(ct);
                    }
                    return 1;
                })
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromMilliseconds(300))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            blockFactory = true;
            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            await Task.Delay(50); // let the scale-up start and block inside the factory

            // Act: dispose while a scale-up's factory call is still pending on the gate.
            var disposeEx = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
            var switchEx = await Record.ExceptionAsync(() => switchTask);

            // Assert: neither disposal nor the pending switch leaks an unhandled exception.
            Assert.Null(disposeEx);
            Assert.Null(switchEx);

            gate.Dispose();
        }

        // ---------------------------------------------------------------------
        // 1.18 - A scale-up in progress must not let sample ticks pile up and drain in a burst right
        // after it finishes. RunSampleTickAsync keeps enqueueing Ticks on its own cadence regardless
        // of whether a scale-up is running. During a slow scale-up, those Ticks pile up in the
        // channel and then drain back-to-back the instant the engine frees up - producing several
        // near-duplicate samples of the post-scale-up idle count, which would trigger an immediate
        // scale-down instead of a properly time-spread one.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_FollowedByEligibleScaleDown_DoesNotEvaluateScaleDownImmediatelyAfter()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleUpThenScaleDownTiming", null);
            var service = builder
                .Factory(_ => Task.Delay(150).ContinueWith(_ => 1))
                .ElasticCapacity(2, 10, 3, 5, TimeSpan.FromSeconds(1))
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty (all 3 items in `held`) - 7 concurrent waiters trigger the backlog-
            // reactive signal (ADR001V03), growing capacity 3 -> 10, possibly over more than one
            // batch (it reacts proportionally to the net gap, not a coarse jump to MaxCapacity). The
            // Monitor's own scale-down path (ADR003V03) is always active for this elastic pool too
            // (ADR001V03/ADR007V03) - no toggle needed. `held` is deliberately NOT released yet:
            // releasing it before the waiters are served would let some of them grab those items
            // directly, shrinking the net gap and making capacity land short of MaxCapacity.
            var waiterTasks = Enumerable.Range(0, 7).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the fault-triggered scale-up to reach max capacity.");
            var scaleUpCompletedAt = DateTime.UtcNow;

            // Every waiter must settle (served or timed out), and any served one disposed, before
            // releasing `held` below - otherwise a leaked never-disposed item would keep the pool
            // from ever becoming genuinely fully idle, the scale-down-eligible condition.
            var waiterResults = await Task.WhenAll(waiterTasks);
            foreach (var result in waiterResults)
            {
                if (result.Successful)
                {
                    await result.DisposeAsync();
                }
            }
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            // Must not have already evaluated (and acted on) a scale-down within a short window of
            // the scale-up completing - that would mean stale/bursty samples, not a fresh window.
            await Task.Delay(300);
            Assert.True(service.IsMaxCapacity, "Expected no scale-down evaluation to complete within 300ms of the scale-up finishing - that would mean the sample window was corrupted by a burst of queued ticks.");

            // It must still eventually happen once a genuinely fresh, time-spread window elapses.
            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.IsMaxCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsMaxCapacity, "Expected the scale-down to still happen once a fresh sample window actually elapsed.");
            Assert.True(DateTime.UtcNow - scaleUpCompletedAt >= TimeSpan.FromMilliseconds(600), "Expected the scale-down to take roughly a full fresh sample window, not fire near-instantly off a burst of stale ticks.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.20 - A scale-down must not block the engine's single-consumer loop while waiting for
        // busy items to be returned. It must only take whatever is already idle right now
        // (opportunistic, partial-if-needed) - the same "keep partial progress" spirit already
        // applied to scale-up - instead of blocking every other command behind a wait.
        //
        // Removal (ADR001V03) note: whether the second Switch below is accepted or rejected is a
        // genuine race, not asserted either way. Every scale-down goes through the same
        // dispatch-then-confirm cycle Creator's scale-up already uses, so whether it beats this
        // second command depends on exact scheduling. What this test actually asserts, and what
        // stays deterministic, is that the engine processes the second command promptly either way,
        // instead of getting stuck behind the first scale-down's wait for busy items.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_WithNotEnoughIdleItems_DoesNotBlockTheEngineForOtherCommands()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleDownDoesNotBlockEngine", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 5, 5, 5, TimeSpan.FromSeconds(2))
                .Build();
            await service.WarmupAsync();

            // Hold 3 of the 5 items - only 2 remain idle, one short of what a scale-down to
            // MinCapacity (2) needs to remove (3).
            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // This posts the scale-down but (without LockWhenScaling) returns as soon as it's
            // accepted, not once the move itself finishes - so it does not, by itself, measure
            // whether the engine is stuck processing it.
            var firstSwitchAccepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            Assert.True(firstSwitchAccepted);

            // A second, independent command posted right after must not be STUCK behind the
            // first one's engine-side processing - the whole point of this test. Whether it is
            // accepted or rejected (see the class remarks in RingBufferContractTests.cs) is a
            // genuine race, not asserted.
            var sw = Stopwatch.StartNew();
            await service.SwitchToAsync(ScaleSwitch.InitCapacity, TimeSpan.FromMinutes(1));
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Expected the engine to process (accept or reject) the second command promptly instead of being stuck behind the first scale-down's wait for busy items - took {sw.Elapsed}.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Accepted as known behavior, not fixed - a manual (pinned) scale-down that only partially
        // completes has nothing retrying it while the pin suppresses the Monitor. Surfaced via a
        // LogWarning instead of staying silent; the pool itself is never corrupted and self-corrects
        // once the pin expires.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_PartialUnderActivePin_LogsWarning()
        {
            var logger = new CapturingLogger();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleDownPartialPinWarning", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .Logger(logger)
                .ElasticCapacity(2, 5, 5, 5, TimeSpan.FromSeconds(2))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Hold 3 of the 5 items - only 2 remain idle, one short of what a scale-down to
            // MinCapacity (2) needs to remove (3).
            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            var reached = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            Assert.False(reached, "Expected the scale-down to only partially complete (3 of 5 items held).");

            lock (logger.Messages)
            {
                Assert.Contains(logger.Messages, m => m.Contains("only partially completed") && m.Contains("manual pin"));
            }

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // v6.0.0 / ADR001V03: the core Creator acceptance criterion - a batch large enough to need
        // more than maxConcurrentFactoryCalls items actually achieves real concurrent fan-out (not
        // just "doesn't block the whole engine"), and never exceeds the configured bound.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_AchievesRealConcurrentFanOut_NeverExceedingMaxConcurrentFactoryCalls()
        {
            var inFlight = 0;
            var maxObserved = 0;
            var maxObservedLock = new object();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBoundedConcurrencyFanOut", null);
            var service = builder
                .Factory(async _ =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    lock (maxObservedLock)
                    {
                        if (now > maxObserved) maxObserved = now;
                    }
                    await Task.Delay(100);
                    Interlocked.Decrement(ref inFlight);
                    return 1;
                })
                .ElasticCapacity(2, 10, 2, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 3)
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // 8 more items are needed (2 -> 10), well beyond the concurrency bound of 3.
            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            Assert.True(moved);
            Assert.Equal(10, service.CurrentCapacity);
            Assert.Equal(3, maxObserved);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // v6.0.0 / ADR001V03: Creator's "simple growing backoff after consecutive [genuine]
        // failures" - persisted across separate creation attempts (repeated
        // Invalidate()-triggered replacements here), not scoped to one batch. Exponential,
        // starting at ~100ms, doubling per consecutive genuine failure, reset by any success -
        // see the class remarks on ApplyFactoryBackoffAsync/_factoryFailureStreak in
        // RingBufferManager. Each replacement is triggered one at a time (a fresh
        // Invalidate()+Dispose() cycle on a still-idle item), waiting for the previous one to be
        // fully processed by the single-consumer engine before triggering the next - the engine's
        // own sequential processing is what guarantees the streak is updated before the next
        // attempt's backoff reads it, no extra synchronization needed.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Factory_BackoffGrows_WithConsecutiveGenuineFailures_AndResetsOnSuccess()
        {
            var replacementTimestamps = new List<DateTime>();
            var warmupDone = false;
            var shouldFail = true;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFactoryBackoffGrows", null);
            var service = builder
                .Factory(_ =>
                {
                    // Only replacement attempts (post-warmup) exercise the fail/backoff behavior -
                    // warmup itself must always succeed cleanly.
                    if (!Volatile.Read(ref warmupDone))
                    {
                        return Task.FromResult(0);
                    }
                    lock (replacementTimestamps) replacementTimestamps.Add(DateTime.UtcNow);
                    if (Volatile.Read(ref shouldFail))
                    {
                        throw new InvalidOperationException("factory down");
                    }
                    return Task.FromResult(1);
                })
                // Elastic, not fixed: a fixed capacity makes MinCapacity == Capacity, so every one
                // of this test's own deliberate failures would also breach the floor guard
                // (ADR001V03) - its own background retry would then call Factory concurrently with
                // this test's next deliberate trigger, corrupting the very timestamps this test
                // reads to measure backoff. MinCapacity=2 (the minimum legal value) gives enough
                // headroom that the 3 consecutive failures below never come close to it.
                .ElasticCapacity(2, 16, 8)
                .Build();
            await service.WarmupAsync();
            warmupDone = true;

            async Task<TimeSpan> TriggerOneReplacementAsync()
            {
                int before;
                lock (replacementTimestamps) before = replacementTimestamps.Count;
                var trigger = DateTime.UtcNow;

                var acquired = await service.AcquireAsync();
                acquired.Invalidate();
                await acquired.DisposeAsync();

                var deadline = DateTime.UtcNow.AddSeconds(3);
                int after;
                while (true)
                {
                    lock (replacementTimestamps) after = replacementTimestamps.Count;
                    if (after > before || DateTime.UtcNow >= deadline) break;
                    await Task.Delay(5);
                }
                Assert.True(after > before, "Expected the replacement's factory call to happen within the deadline.");
                DateTime lastCall;
                lock (replacementTimestamps) lastCall = replacementTimestamps[^1];
                return lastCall - trigger;
            }

            // streak 0 going in (no prior failure) -> ~no backoff; this attempt fails -> streak 1.
            var delay1 = await TriggerOneReplacementAsync();
            // streak 1 going in -> ~100ms backoff; fails -> streak 2.
            var delay2 = await TriggerOneReplacementAsync();
            // streak 2 going in -> ~200ms backoff; fails -> streak 3.
            var delay3 = await TriggerOneReplacementAsync();

            Assert.True(delay1 < TimeSpan.FromMilliseconds(250), $"Expected ~no backoff on the very first attempt; took {delay1}.");
            Assert.True(delay2 > delay1, $"Expected backoff to kick in after the first failure: delay1={delay1}, delay2={delay2}.");
            Assert.True(delay2 < TimeSpan.FromMilliseconds(700), $"Expected roughly ~100ms backoff after 1 failure, not something far larger; took {delay2}.");
            Assert.True(delay3 > delay2, $"Expected backoff to keep growing: delay2={delay2}, delay3={delay3}.");
            Assert.True(delay3 < TimeSpan.FromMilliseconds(1200), $"Expected roughly ~200ms backoff after 2 failures, not something far larger; took {delay3}.");

            // A success resets the streak: let this next attempt succeed (whatever backoff it
            // still pays for streak 3 going in), then the one right after it must be back to
            // ~no backoff, not continuing to grow from streak 3.
            shouldFail = false;
            _ = await TriggerOneReplacementAsync();
            shouldFail = true;
            var delayAfterReset = await TriggerOneReplacementAsync();

            Assert.True(delayAfterReset < TimeSpan.FromMilliseconds(250), $"Expected the streak to have reset after a success; took {delayAfterReset} (would be ~400ms+ if it had not).");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // v6.0.0 / ADR001V03: the backoff wait must never be counted against a
        // scale-up's own quantity * FactoryTimeout deadline (`overall` in CreateItemsAsync) - an
        // elevated streak's backoff (capped at 5s) could otherwise exceed a short deadline and
        // cancel the attempt before Factory is ever called even once, self-inflicting exactly the
        // "routine scale-up structurally impossible" failure that deadline formula exists to
        // prevent, on an otherwise perfectly healthy factory. min=2/init=2/max=3 keeps every
        // scale-up in this test needing exactly 1 item, so its deadline is exactly one
        // FactoryTimeout throughout - deliberately shorter than the streak-3 backoff (~400ms)
        // built up beforehand.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_WithElevatedBackoffStreak_StillGetsARealAttempt_WithinItsOwnTightDeadline()
        {
            var scalingUp = false;
            var factoryShouldFail = true;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBackoffDoesNotEatScaleUpDeadline", null);
            var service = builder
                .Factory(_ =>
                {
                    if (!Volatile.Read(ref scalingUp))
                    {
                        return Task.FromResult(0); // warmup always succeeds
                    }
                    if (Volatile.Read(ref factoryShouldFail))
                    {
                        throw new InvalidOperationException("factory down");
                    }
                    return Task.FromResult(1);
                }, TimeSpan.FromMilliseconds(300))
                .ElasticCapacity(2, 3, 2, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();
            scalingUp = true;

            // Build a failure streak of 3 via 3 failed 1-item scale-up attempts (capacity never
            // moves off 2, since a totally-failed batch does not decrement it) - each is a
            // separate CreateItemsAsync call, so the streak persists across them. A total failure
            // (quantity 1, 0 created) throws rather than returning false - the same established
            // contract as SwitchToAsync_WhenFactoryThrowsDuringScaleUp_PropagatesRealException_AndEngineSurvives.
            for (var i = 0; i < 3; i++)
            {
                var ex = await Record.ExceptionAsync(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)));
                Assert.IsType<InvalidOperationException>(ex);
                Assert.True(service.IsInitCapacity);
            }

            // Streak is now 3 (this next attempt's backoff: ~400ms). The factory itself is now
            // healthy; this scale-up's own deadline (quantity 1 * FactoryTimeout 300ms = 300ms) is
            // deliberately shorter than that backoff.
            factoryShouldFail = false;
            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

            Assert.True(moved, "Expected the scale-up to still get a real attempt despite an elevated backoff streak longer than its own deadline.");
            Assert.True(service.IsMaxCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // The plain-text scale log messages
        // ("Starting ScaleUp N."/"End ScaleUp.") must interpolate scaleTrigger, even though it is
        // already in scope - without it, a consumer using only ILogger (no Meter/Activity
        // listener) could not tell a manual switch apart from a
        // floor-guard/backlog-reactive/Monitor-driven scale from the log stream alone.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleLogMessages_IncludeTheTriggerThatCausedThem()
        {
            var logger = new CapturingLogger();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleLogIncludesTrigger", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(logger)
                .ElasticCapacity(2, 5, 2)
                .LockWhenScaling()
                .BuildWarmupAsync();

            var reached = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(5));
            Assert.True(reached);

            lock (logger.Messages)
            {
                Assert.Contains(logger.Messages, m => m.Contains("Starting ScaleUp") && m.Contains("Trigger: manual"));
                Assert.Contains(logger.Messages, m => m.Contains("End ScaleUp") && m.Contains("Trigger: manual"));
            }

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_WhenAnIdleItemsDisposeHangsForever_StillLetsTheEngineProcessLaterCommands()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractScaleDownHangingDispose", null);
            var service = builder
                .Factory(_ => Task.FromResult(new HangingDisposeProbe(releaseHang)))
                .ElasticCapacity(2, 4, 4, 1, TimeSpan.FromSeconds(5))
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(200))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Scale down 4 -> 2: RemoveItemsAsync pulls 2 idle items whose Dispose() hangs forever.
            var switchTask = service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            var completed = await Task.WhenAny(switchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(switchTask, completed);

            // The engine must still be alive for further work - a second, unrelated command must
            // not be stuck behind the first scale-down's hung item disposals.
            var secondSwitchTask = service.SwitchToAsync(ScaleSwitch.InitCapacity, TimeSpan.FromMinutes(1));
            var secondCompleted = await Task.WhenAny(secondSwitchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(secondSwitchTask, secondCompleted);

            releaseHang.Set();
            var disposeTask = service.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(disposeTask, disposeCompleted);
        }

        // ---------------------------------------------------------------------
        // Removal (ADR001V03): the test above proves the engine survives a hung scale-down disposal
        // within DisposeAsync()'s own drain (already true before this role existed, thanks to the
        // pre-existing PulseHeartBeat grace-period bound). What it does NOT prove is that the
        // engine stays FREE while that grace period is still being waited out - a scale-down's
        // disposal ran INLINE on the engine's own single-consumer thread before this role isolated
        // it, so an entirely unrelated command (ReplaceOne, gated by nothing scale-related) queued
        // right behind a hung scale-down still had to wait out the full grace period before the
        // engine could even dequeue it. This test races a ReplaceOne against a scale-down whose
        // disposal never releases within the test's own window, and asserts the replacement's own
        // factory call - genuinely unrelated to the scale-down - happens promptly instead of being
        // stuck behind it.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_WhenDisposalHangs_StillLetsAnUnrelatedReplaceOneRunPromptly()
        {
            using var releaseHang = new ManualResetEventSlim();
            var callCount = 0;
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractScaleDownDisposalDoesNotBlockReplaceOne", null);
            var service = await builder
                .Factory(_ =>
                {
                    Interlocked.Increment(ref callCount);
                    return Task.FromResult(new HangingDisposeProbe(releaseHang));
                })
                .ElasticCapacity(2, 4, 4, 1, TimeSpan.FromSeconds(30))
                .BuildWarmupAsync();
            Assert.Equal(4, Volatile.Read(ref callCount));

            // Fire-and-forget: without LockWhenScaling, this returns as soon as it's accepted -
            // well before the engine even starts, let alone finishes, dequeuing/disposing the 2
            // idle items this scale-down needs to remove (both HangingDisposeProbe instances,
            // never released within this test's own window).
            _ = service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            // Give the engine a moment to actually dequeue and start processing the Switch command
            // before racing it with the unrelated ReplaceOne below.
            await Task.Delay(50);

            var acquired = await service.AcquireAsync();
            var sw = Stopwatch.StartNew();
            acquired.Invalidate();
            // Deliberately NOT awaited: TurnbackAsync's own Invalidate branch posts ReplaceOne to
            // the engine BEFORE awaiting the old (also-hanging) item's own DisposeAsync() - so the
            // caller's own await on that call is itself bound by that same hang, a separate,
            // already-covered concern (see the DisposeAsync_WhenAnIdleItemsDisposeHangsForever_...
            // tests) unrelated to what this test measures. Only the ENGINE's own processing of the
            // already-posted ReplaceOne command is under test here.
            var oldItemDisposeTask = acquired.DisposeAsync().AsTask();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref callCount) < 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            sw.Stop();
            Assert.Equal(5, Volatile.Read(ref callCount));
            // Budget is generous (well below the 10s grace period, but far above what a prompt
            // completion needs) to absorb scheduling jitter on shared CI runners.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected the unrelated ReplaceOne's own factory call to happen promptly instead of being stuck behind the scale-down's own hung-disposal grace period (PulseHeartBeat default, 10s). Actual: {sw.Elapsed}.");

            releaseHang.Set();
            await oldItemDisposeTask;
            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_WhenFactoryTimesOut_ActuallyCancelsTheAbandonedFactoryCall()
        {
            var callCount = 0;
            var cancelledPromptly = new TaskCompletionSource<bool>();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleUpFactoryTokenPropagation", null);
            var service = await builder
                .Factory(async ct =>
                {
                    if (Interlocked.Increment(ref callCount) <= 2)
                    {
                        return 1; // the ElasticCapacity warmup fill (initial = 2)
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
                .ElasticCapacity(2, 4, 2)
                .BuildWarmupAsync();

            _ = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)); // triggers CreateItemsAsync(quantity: 2)

            var completed = await Task.WhenAny(cancelledPromptly.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(cancelledPromptly.Task, completed);

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task FloorGuard_DuringAnActivePin_StillRestoresCapacity()
        {
            var shouldFail = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFloorGuardDuringPin", null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            // Pin to MaxCapacity for far longer than this whole test takes - ADR007V03: the floor
            // guard must still act while a pin is active, since it is never suppressed by one.
            var pinSetAt = DateTime.UtcNow;
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(10));
            Assert.True(accepted);
            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity < 6 && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(6, service.CurrentCapacity);

            // Drive capacity below MinCapacity (2) while the pin above is still active: invalidate
            // idle items one at a time while the factory is failing, so each ReplaceOne's own
            // replacement attempt fails and capacity only ever shrinks. Polling between each one
            // keeps this from racing ahead of the engine's own async ReplaceOne processing.
            shouldFail = true;
            for (var i = 0; i < 5; i++)
            {
                var before = service.CurrentCapacity;
                var acquired = await service.AcquireAsync();
                acquired.Invalidate();
                await acquired.DisposeAsync();
                var shrinkDeadline = DateTime.UtcNow.AddSeconds(2);
                while (service.CurrentCapacity >= before && DateTime.UtcNow < shrinkDeadline)
                {
                    await Task.Delay(20);
                }
            }
            Assert.True(service.CurrentCapacity < 2, $"Expected capacity to breach MinCapacity. Actual: {service.CurrentCapacity}.");

            shouldFail = false;
            var healDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity < 2 && DateTime.UtcNow < healDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(2, service.CurrentCapacity);
            Assert.True(DateTime.UtcNow - pinSetAt < TimeSpan.FromSeconds(10), "Test took too long relative to the pin's own duration - it may have already expired, weakening this test's proof that the floor guard is not gated by it.");

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task BacklogReactive_DuringAnActivePin_StillScalesUp()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBacklogDuringPin", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 10, 4, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(500))
                .Build();
            await service.WarmupAsync();

            // Pin down to MinCapacity for far longer than this whole test takes - ADR007V03:
            // backlog-reactive must still act while this pin is active, since it is never
            // suppressed by one.
            var pinSetAt = DateTime.UtcNow;
            var accepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(10));
            Assert.True(accepted);
            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity > 2 && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(2, service.CurrentCapacity);

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            var waiterTasks = Enumerable.Range(0, 3).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity == 2 && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.CurrentCapacity > 2, $"Expected the backlog-reactive signal to scale up despite an active pin. Actual: {service.CurrentCapacity}.");
            Assert.True(DateTime.UtcNow - pinSetAt < TimeSpan.FromSeconds(10), "Test took too long relative to the pin's own duration - it may have already expired, weakening this test's proof that backlog-reactive is not gated by it.");

            await Task.WhenAll(waiterTasks);
            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await service.DisposeAsync();
        }
    }
}
