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
        public async Task HeartbeatTriggeredReplacement_WhenFactoryThrowsOperationCanceledException_LogsTheRealException_NotAFabricatedTimeout()
        {
            var errors = new List<Exception>();
            var replacementShouldThrow = false;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractReplacementFactoryThrowsOce", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new TaskCanceledException("factory's own unrelated timeout") : Task.FromResult(1))
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            replacementShouldThrow = true;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!errors.Any(e => e is TaskCanceledException) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Contains(errors, e => e is TaskCanceledException && e.Message == "factory's own unrelated timeout");
            Assert.DoesNotContain(errors, e => e is TimeoutException && e.Message == "Timeout factory (replacement)");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.10 - RingBufferManager.DisposeAsync's own _disposed guard has the same non-atomic
        // check-then-set race shape as RingBufferValue's - fixed via the identical
        // Interlocked.Exchange idiom used there.
        // ---------------------------------------------------------------------

        // ---------------------------------------------------------------------
        // 1.11 - A HeartBeat callback that blocks past its pulse budget must not stop the heartbeat
        // pump forever, and the item it was holding must not be lost. A CancellationToken passed
        // to Task.Run only prevents the delegate from starting - it does not cancel a delegate once
        // running, so bounding a blocking callback requires a different mechanism than relying on
        // that token alone.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_CallbackBlocksPastPulseBudget_DoesNotStopThePump_AndInvalidatesTheItem()
        {
            var invocations = 0;
            var blockedOnce = false;
            using var release = new ManualResetEventSlim(false);
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractHeartBeatBlocking", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .HeartBeat(_ =>
                {
                    Interlocked.Increment(ref invocations);
                    if (!blockedOnce)
                    {
                        blockedOnce = true;
                        // Blocks well past the pulse budget on the first call only, and is never
                        // released until this test's cleanup - simulating a callback that hangs
                        // indefinitely (e.g. a dead socket read with no timeout).
                        release.Wait();
                    }
                    return true;
                }, TimeSpan.FromMilliseconds(50))
                .FixedCapacity(2)
                .Build();
            try
            {
                await service.WarmupAsync();

                // A second (and subsequent) pulse must happen within a couple of pulse budgets even
                // though the first invocation is still blocked - the pump must not be blocked by
                // one hung callback.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (Volatile.Read(ref invocations) < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
                Assert.True(Volatile.Read(ref invocations) >= 2, "Heartbeat pump stopped after the first invocation blocked past its pulse budget.");

                // Both items must still be available - the timed-out one was replaced, not
                // permanently lost. (Capacity is 2 so one item is always free regardless; acquiring
                // both is what actually proves a replacement exists.)
                var first = await service.AcquireAsync();
                Assert.True(first.Successful);
                var secondTask = service.AcquireAsync().AsTask();
                var secondCompleted = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.Same(secondTask, secondCompleted);
                Assert.True((await secondTask).Successful);
            }
            finally
            {
                release.Set();
                await service.DisposeAsync();
            }
        }

        // ---------------------------------------------------------------------
        // RunHeartbeatAsync's own `await acquired.DisposeAsync()` runs on the healthy-verdict path
        // (separate from the timeout branch below it, which is already bounded). When the verdict is
        // `false`, that call routes through TurnbackAsync's Invalidate branch, which awaits the old
        // item's own Dispose()/DisposeAsync() directly - and that await is unbounded. If it hangs, it
        // blocks _heartbeatTask forever. DisposeAsync() awaits _heartbeatTask before its own cleanup
        // (draining _availableItems, disposing _lifetime/_meter/_activitySource), so the manager's
        // DisposeAsync() never returns either, and a retry is a permanent silent no-op.
        //
        // The sibling case above (Invalidate_WhenItemDisposeHangs_StillReplacesTheSlot_WithoutWaitingForIt)
        // is different: there, a hang is local to that one caller's own DisposeAsync() call. Here,
        // "the caller" is the framework's own heartbeat pump, so its hang becomes everyone's problem.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeatInvalidate_WhenItemDisposeHangs_ManagerDisposeAsyncStillReturnsPromptly()
        {
            using var release = new ManualResetEventSlim(false);
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractHeartbeatInvalidateHangingDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingDisposeProbe(release)))
                .HeartBeat(_ => false, pulse: TimeSpan.FromMilliseconds(50))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // Let at least one pulse fire, acquire an idle item, get the unhealthy verdict, and
            // start hanging inside its Dispose() via TurnbackAsync's Invalidate branch.
            await Task.Delay(300);

            try
            {
                var disposeTask = service.DisposeAsync().AsTask();
                var completedInTime = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2))) == disposeTask;
                Assert.True(completedInTime, "Expected the manager's DisposeAsync() to return promptly even though the heartbeat-invalidated item's own Dispose() is hanging.");
            }
            finally
            {
                // Let the probe's background dispose finish so it doesn't linger past the test.
                release.Set();
            }
        }

        // ---------------------------------------------------------------------
        // The deferred dispose in the branch fixed above was not fault-observed. Unlike the sibling
        // branch nearby (which wraps its deferred DisposeItemAsync in a ContinueWith that logs a
        // fault), the raw disposeTask here was added straight into _pendingHeartbeatDisposals. A
        // late failure from the item's own Dispose() - after it had already been deferred past one
        // PulseHeartBeat - reached neither LogError/OnError nor TaskScheduler's unobserved-exception
        // handling. It was silently dropped, unlike every other deferred-dispose path in this file.
        // ---------------------------------------------------------------------

        // Per-instance hang flag. It is deliberately NOT one shared ManualResetEventSlim across every
        // pooled item: the other idle item still in the pool at DisposeAsync() time already goes
        // through DisposeOneItemDefensivelyAsync, which fault-observes correctly. Sharing one trigger
        // across both items would let that already-correct path mask a bug in the path this test
        // targets. Only the specific instance the HeartBeat callback marks ever hangs or throws;
        // every other instance's DisposeAsync is a no-op.
        private sealed class SelectivelyHangingThenThrowingDisposeProbe : IAsyncDisposable
        {
            private readonly ManualResetEventSlim _release = new();
            public bool ShouldHang;

            public async ValueTask DisposeAsync()
            {
                if (!ShouldHang) return;
                await Task.Run(() => _release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                throw new InvalidOperationException("Simulated late dispose failure after grace period.");
            }

            public void Release() => _release.Set();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeatInvalidate_WhenDeferredItemDisposeLaterFaults_IsStillDelivered_NotSilentlyDropped()
        {
            var errors = new List<Exception>();
            SelectivelyHangingThenThrowingDisposeProbe? hungProbe = null;

            IRingBufferBuilder<SelectivelyHangingThenThrowingDisposeProbe> builder = new RingBufferBuilder<SelectivelyHangingThenThrowingDisposeProbe>("ContractHeartbeatDeferredDisposeFaultNotDropped", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new SelectivelyHangingThenThrowingDisposeProbe()))
                .Logger(new CapturingLogger())
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .HeartBeat(item =>
                {
                    item.ShouldHang = true;
                    hungProbe = item;
                    return false;
                }, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // Let a pulse fire, invalidate the item it acquires, and start hanging inside its
            // Dispose() - by 500ms the pump has already deferred it (its own PulseHeartBeat=200ms
            // elapsed) into _pendingHeartbeatDisposals, without the hang being released yet.
            await Task.Delay(500);
            Assert.NotNull(hungProbe);

            // DisposeAsync() now starts its OWN PulseHeartBeat-bounded grace period for that same
            // deferred item - still not released, so it also times out and DisposeAsync() gives up
            // and returns, never itself observing the eventual fault. The other, still-idle item
            // was never marked ShouldHang, so it disposes as a no-op and cannot mask the result.
            await service.DisposeAsync();

            // Only now does the deferred dispose actually resolve - and fault - strictly after
            // DisposeAsync() itself has already returned.
            hungProbe!.Release();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (errors)
                {
                    if (errors.Any(e => e is InvalidOperationException ioe && ioe.Message.Contains("Simulated late dispose failure"))) break;
                }
                await Task.Delay(20);
            }
            lock (errors)
            {
                Assert.Contains(errors, e => e is InvalidOperationException ioe && ioe.Message.Contains("Simulated late dispose failure"));
            }
        }

        // ---------------------------------------------------------------------
        // 1.21 - The heartbeat pump's own internal AcquireAsync call must not count toward the
        // autoscale fault budget: it is an internal health check, not consumer demand, and letting
        // its timeout trigger a scale-up is a self-inflicted false signal.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_AcquireTimeoutWhilePoolIsExhausted_DoesNotCountTowardAutoScaleFaultBudget()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractHeartbeatDoesNotFault", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .HeartBeat(_ => true, TimeSpan.FromMilliseconds(100))
                .ElasticCapacity(2, 4, 2, 5, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(100))
                .Build();
            await service.WarmupAsync();

            // Hold both items - the pool is empty, so every heartbeat pulse's own internal
            // AcquireAsync call times out. With NumberFault=0 (fires on the very first fault), a
            // single one of these would be enough to trigger a scale-up if it counted.
            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 2; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Let several heartbeat pulses (every 100ms) elapse while the pool stays exhausted.
            await Task.Delay(500);
            Assert.False(service.IsMaxCapacity, "Expected the heartbeat's own internal acquire timeouts to NOT trigger an autoscale scale-up - only external consumer demand should count toward the fault budget.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.22 - A heartbeat callback that blocks past its pulse budget must not have the pooled
        // resource disposed out from under it. The orphaned callback keeps running on its own
        // thread-pool thread and may still be touching the resource when the timeout fires -
        // disposing it right away would be a genuine use-after-dispose race on the caller's own
        // object (a DB connection, a RabbitMQ channel), not just internal bookkeeping.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_CallbackBlocksPastPulseBudget_DoesNotDisposeTheResourceWhileStillInUse()
        {
            // FixedCapacity(2) means a 2nd heartbeat cycle can acquire the other slot while the
            // 1st is still blocked - each Factory call must return a genuinely distinct instance
            // (a shared instance would make an Invalidate()-triggered "replacement" secretly be
            // the very same already-disposed object, contaminating this test with an unrelated
            // false positive). Only the 1st cycle's probe is the one under test; later cycles
            // just no-op past the guard below.
            DisposableProbe? firstProbe = null;
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<DisposableProbe> builder = new RingBufferBuilder<DisposableProbe>("ContractHeartbeatDisposeRace", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new DisposableProbe()))
                .HeartBeat(value =>
                {
                    if (Interlocked.CompareExchange(ref firstProbe, value, null) is not null
                        && !ReferenceEquals(firstProbe, value))
                    {
                        return true;
                    }
                    callbackStarted.Set();
                    // Block well past the pulse budget - simulates a callback that cannot be
                    // cancelled and keeps running (and touching the resource) after the manager
                    // has already given up waiting on it.
                    releaseCallback.Wait(TimeSpan.FromSeconds(5));
                    value.Touch();
                    return true;
                }, pulse: TimeSpan.FromMilliseconds(100))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the pulse timeout (100ms) fire while the callback is still blocked.
            await Task.Delay(400);

            releaseCallback.Set();
            // Give the callback time to wake up and call Touch().
            await Task.Delay(300);

            Assert.NotNull(firstProbe);
            Assert.False(firstProbe!.TouchedAfterDispose, "Expected the resource to still be usable by the orphaned callback at the moment it touches it - it must not have been disposed while the callback might still be using it.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.31 - _pendingHeartbeatDisposals (the deferred-dispose bag) must not only be pruned by
        // DisposeAsync itself, at the very end of the buffer's life - a HeartBeat callback that
        // chronically overran its own pulse budget would otherwise add one entry per timed-out
        // pulse for as long as the buffer stayed alive, even though almost every one of those
        // entries had already completed by the time the next pulse timed out. Unbounded growth
        // for the buffer's entire runtime, not a correctness bug. Fixed by pruning
        // already-completed entries out of the bag every time a new one is added.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task PendingHeartbeatDisposals_UnderAChronicallySlowHeartbeat_DoesNotGrowUnbounded()
        {
            var callbackCount = 0;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPendingHeartbeatDisposalsBounded", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .HeartBeat(_ =>
                {
                    Interlocked.Increment(ref callbackCount);
                    // Always slower than the 100ms pulse below, so every single pulse times out
                    // and enqueues a deferred-dispose entry - but still fast enough that each
                    // entry finishes well before the next one is added.
                    Thread.Sleep(180);
                    return true;
                }, pulse: TimeSpan.FromMilliseconds(100))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref callbackCount) < 6 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.True(callbackCount >= 6, $"Expected at least 6 heartbeat callbacks, got {callbackCount}.");

            // Give the most recent deferred dispose a moment to finish too, so this reads the
            // steady-state bag size rather than catching it mid-cycle.
            await Task.Delay(50);

            var pending = (List<Task>)GetPrivateField(service, "_pendingHeartbeatDisposals");
            int pendingCount;
            lock (GetPrivateField(service, "_pendingHeartbeatDisposalsGate"))
            {
                pendingCount = pending.Count;
            }
            Assert.True(pendingCount <= 2, $"Expected the deferred-disposal list to stay bounded despite {callbackCount} timed-out pulses, but it grew to {pendingCount} entries.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.32 - No call to a user-supplied Logger/ErrorHandler was guarded against that callback
        // itself throwing. On the heartbeat-timeout path, a throwing OnError - invoked from LogError
        // right before the ReplaceOne command is enqueued - aborted the whole catch block before
        // ReplaceOne ever ran. That permanently lost one pool slot and faulted _heartbeatTask.
        // DisposeAsync() then observed that fault, called LogError(ex) to report it, which invoked
        // the same throwing OnError again - this time uncaught, making DisposeAsync() itself throw.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeatTimeout_WithAThrowingOnErrorHandler_StillReplacesTheStuckItemAndDisposesCleanly()
        {
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<DisposableProbe> builder = new RingBufferBuilder<DisposableProbe>("ContractHeartbeatThrowingOnError", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new DisposableProbe()))
                .OnError(_ => throw new InvalidOperationException("user OnError sink bug"))
                .HeartBeat(_ =>
                {
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    return true;
                }, pulse: TimeSpan.FromMilliseconds(200))
                .AcquireTimeout(TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 200ms pulse timeout fire while the callback is still blocked - this is what
            // triggers the throwing OnError call inside RunHeartbeatAsync's timeout catch.
            await Task.Delay(500);

            // The stuck slot must still get replaced despite OnError throwing - both items must be
            // acquirable, proving the pool wasn't left permanently one item short.
            var first = await service.AcquireAsync();
            var second = await service.AcquireAsync();
            Assert.True(first.Successful, "Expected the first item to still be acquirable.");
            Assert.True(second.Successful, "Expected the replacement item to be acquirable despite the throwing OnError handler.");
            await first.DisposeAsync();
            await second.DisposeAsync();

            releaseCallback.Set();
            await service.DisposeAsync(); // must not throw, even with a permanently-broken OnError handler
        }

        // ---------------------------------------------------------------------
        // 1.33 - Each SafeInvokeSink call is independent, but this proves it end-to-end -
        // a throwing OnError on one heartbeat-triggered invocation must not prevent later,
        // separate invocations of the same handler from still firing normally. Originally written
        // against BackgroundLogger(true)'s own dispatch loop, which a throwing OnError could fault
        // permanently (unguarded, dropping every later message silently); that loop no longer
        // exists (ADR007V03 removed BackgroundLogger entirely), but the black-box behavior this
        // test observes is unrelated to that mechanism and still worth guarding.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_WhenOnErrorThrowsOnce_StillDeliversLaterInvocations()
        {
            var callCount = 0;
            var delivered = new List<Exception>();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractOnErrorThrowsOnceStillDeliversLater", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(new CapturingLogger())
                .OnError(ex =>
                {
                    if (Interlocked.Increment(ref callCount) == 1)
                    {
                        throw new InvalidOperationException("user OnError sink bug");
                    }
                    lock (delivered) delivered.Add(ex);
                })
                .HeartBeat(_ => throw new InvalidOperationException("heartbeat callback boom"), pulse: TimeSpan.FromMilliseconds(80))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref callCount) < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.True(callCount >= 3, $"Expected at least 3 OnError invocations (a throw on one must not prevent later ones), got {callCount}.");
            Assert.NotEmpty(delivered);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // HeartBeat redesign (ADR007V03): the callback now receives the raw T and returns a bool
        // instead of the whole RingBufferValue<T> - false discards the item (a replacement is
        // created in its place, through the same path Invalidate() uses), true keeps it.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_WhenCallbackReturnsFalse_DiscardsAndReplacesTheItem()
        {
            var seenValues = new System.Collections.Concurrent.ConcurrentBag<int>();
            var callCount = 0;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractHeartBeatFalseDiscards", null);
            var service = await builder
                .Factory(_ => Task.FromResult(Interlocked.Increment(ref callCount)))
                .HeartBeat(value =>
                {
                    seenValues.Add(value);
                    return false; // always unhealthy - every tick must get a freshly-created item
                }, pulse: TimeSpan.FromMilliseconds(50))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (seenValues.Count < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(seenValues.Count >= 3, $"Expected at least 3 heartbeat ticks, got {seenValues.Count}.");
            // Every discarded item is replaced, never returned to the pool - the same physical item
            // (identified here by its factory-assigned value) must never be inspected twice.
            Assert.Equal(seenValues.Count, seenValues.Distinct().Count());

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_WhenCallbackReturnsTrue_ReturnsTheSameItemToThePool()
        {
            var callCount = 0;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractHeartBeatTrueKeeps", null);
            var service = await builder
                .Factory(_ => Task.FromResult(Interlocked.Increment(ref callCount)))
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(50))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // With nothing else competing for either item, several pulses (50ms each) must keep
            // inspecting the same 2 original items if `true` genuinely keeps them - any replacement
            // would show up as an extra Factory call beyond the initial 2-item warmup fill.
            await Task.Delay(300);

            Assert.Equal(2, Volatile.Read(ref callCount));

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.42 - A comment above the backlog-reactive signal in
        // AcquireCoreAsync claimed the heartbeat's own internal acquire "must not count" toward
        // it, but only the EngineCommand.Backlog() dispatch was actually gated by
        // countsTowardFaultBudget - Interlocked.Increment(ref _waitingCount) itself ran
        // unconditionally, so a heartbeat blocked waiting for an item still inflated the same
        // counter EvaluateBacklogReactive/ProcessTick read to size a scale-up. Fixed by gating the
        // increment (and its matching decrement) on countsTowardFaultBudget too, so the comment's
        // stated intent actually holds.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartbeatAcquireWaiting_DoesNotInflateWaitingCount()
        {
            var manager = CreateFixedManager(1, _ => Task.FromResult(1));
            await manager.WarmupAsync();

            // Drain the only item so the heartbeat-style acquire below genuinely blocks on ReadAsync.
            var held = await manager.AcquireAsync();

            var acquireForHeartbeatAsync = manager.GetType().GetMethod("AcquireForHeartbeatAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Method 'AcquireForHeartbeatAsync' not found.");
            var heartbeatTask = (ValueTask<RingBufferValue<int>>)acquireForHeartbeatAsync.Invoke(manager, [CancellationToken.None])!;

            // Give the heartbeat-style acquire time to actually reach the blocking ReadAsync.
            await Task.Delay(50);
            Assert.Equal(0, (int)GetPrivateField(manager, "_waitingCount"));

            await held.DisposeAsync(); // returns the item, unblocking the heartbeat-style acquire
            var heartbeatValue = await heartbeatTask;
            await heartbeatValue.DisposeAsync();
            await manager.DisposeAsync();
        }
    }
}
