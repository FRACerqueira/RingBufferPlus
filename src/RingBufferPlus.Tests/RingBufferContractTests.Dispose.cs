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
        // 1.4 - DisposeAsync is idempotent and background tasks end without an unhandled fault
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_IsIdempotent_AndBackgroundTasksEndWithoutUnhandledFault()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeSafety", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .HeartBeat(_ => true)
                .FixedCapacity(4)
                .Build();
            await service.WarmupAsync();

            // Act: dispose twice.
            var firstDispose = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
            var secondDispose = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());

            // Assert: idempotent.
            Assert.Null(firstDispose);
            Assert.Null(secondDispose);

            // Assert: none of the background tasks ended in a faulted state.
            foreach (var fieldName in new[] { "_engineTask", "_heartbeatTask", "_sampleTickTask" })
            {
                if (GetPrivateField(service, fieldName) is Task task)
                {
                    Assert.False(task.IsFaulted, $"{fieldName} faulted: {task.Exception}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // 1.4b - Disposing while a warmup is still in flight must not leak a faulted,
        // unawaited background task.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_DuringInFlightWarmup_DoesNotLeakFaultedBackgroundTasks()
        {
            // Arrange: a slow factory widens the window for DisposeAsync to race the warmup.
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringWarmup", null);
            var service = builder
                .Factory(async _ => { await Task.Delay(50); return 1; })
                .HeartBeat(_ => true)
                .FixedCapacity(4)
                .Build();

            var warmupTask = service.WarmupAsync();

            // Act: dispose without waiting for warmup to settle.
            var disposeEx = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
            // Drain the warmup task's outcome (it may legitimately fail once disposed mid-flight) so it
            // is observed and cannot surface as an unobserved task exception later.
            _ = await Record.ExceptionAsync(() => warmupTask);

            // Assert: disposal itself never throws...
            Assert.Null(disposeEx);

            // ...and every background pump WarmupCoreAsync might have started is accounted for and clean.
            foreach (var fieldName in new[] { "_engineTask", "_heartbeatTask", "_sampleTickTask" })
            {
                if (GetPrivateField(service, fieldName) is Task task)
                {
                    Assert.False(task.IsFaulted, $"{fieldName} faulted: {task.Exception}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // 1.9 - A command whose completion signal races DisposeAsync's cancellation must not hang
        // forever if cancellation wins. This is a genuine data race inside Channel<T>, and it can't
        // be forced to a single deterministic outcome from outside the channel - so this test runs
        // as a bounded stress loop instead of a one-shot repro.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnUnawaitedWarmup_NeverHangs()
        {
            for (var i = 0; i < 10; i++)
            {
                var manager = CreateFixedManager(2, _ => Task.FromResult(1));

                var warmupTask = manager.WarmupAsync(); // fired, deliberately not awaited
                var disposeTask = manager.DisposeAsync().AsTask(); // races it immediately

                var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.True(ReferenceEquals(disposeTask, disposeCompleted), $"DisposeAsync hung on iteration {i}.");
                await disposeTask;

                var warmupCompleted = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.True(ReferenceEquals(warmupTask, warmupCompleted), $"WarmupAsync hung on iteration {i}.");
                _ = await Record.ExceptionAsync(() => warmupTask);
            }
        }

        // ---------------------------------------------------------------------
        // 1.12 - Disposal must always drain and clean up regardless of how a background pump
        // ended. Any fault from a background pump - not just OperationCanceledException - must
        // not escape DisposeAsync before draining pooled items and disposing
        // _lifetime/_meter/_activitySource.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnActiveHeartbeat_NeverThrowsUnhandled()
        {
            const int attempts = 400;
            var exceptions = new List<Exception>();

            for (var i = 0; i < attempts; i++)
            {
                IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringHeartbeat", null);
                var service = builder
                    .Factory(_ => Task.FromResult(1))
                    .HeartBeat(_ => true, TimeSpan.FromMilliseconds(15))
                    .FixedCapacity(2)
                    .Build();
                await service.WarmupAsync();

                // Dispose at a varied point relative to the 15ms pulse, not immediately - the race
                // is between the heartbeat pump's Task.Delay elapsing (not being cancelled) and
                // _disposed/_lifetime flipping right as it moves on to its own AcquireAsync call.
                await Task.Delay(Random.Shared.Next(10, 41));

                var ex = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());
                if (ex is not null)
                {
                    exceptions.Add(ex);
                }
            }

            Assert.Empty(exceptions);
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task TurnbackAsync_AfterManagerDisposed_DisposesTheItem_InsteadOfLeakingIt()
        {
            var probe = new DisposableProbe();
            var manager = new RingBufferManager<DisposableProbe>(default)
            {
                Name = "ContractTurnbackAfterDispose",
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

            // Held while the manager is disposed - the pool still contains one other reference to
            // the same probe, which the drain loop disposes; `held` is returned only afterward.
            var held = await manager.AcquireAsync();
            await manager.DisposeAsync();

            await held.DisposeAsync();

            Assert.Equal(2, probe.DisposeCount);
        }

        // _disposed flips to true synchronously as
        // the first step of DisposeAsync, but _lifetime.Dispose() only runs once its finally has
        // awaited in-flight engine/heartbeat/sample-tick work - a caller that already passed
        // AcquireCoreAsync/SwitchToAsync/WarmupCoreAsync's ObjectDisposedException.ThrowIf(_disposed,
        // this) guard can still observe _lifetime already disposed by the time it reaches
        // _lifetime.Token, which throws ObjectDisposedException with the CancellationTokenSource's
        // identity instead of this manager's - a correct exception type with a confusing identity.
        // No corruption, leak, or hang results (TurnbackAsync already handles the concurrent-drain
        // ChannelClosedException case) - only a misleading exception message under an otherwise
        // ordinary graceful shutdown racing a caller.
        //
        // The race window itself is a handful of CPU instructions between two field reads inside
        // the same async method and cannot be forced deterministically without instrumenting
        // production code, so a true end-to-end red-then-green against the original bug is not
        // achievable here (a probabilistic stress test racing AcquireAsync against DisposeAsync
        // does reproduce it, but is too flaky/slow to commit as a permanent regression test).
        // This test instead pins the fix's actual contract
        // deterministically: reflectively invoke the private LifetimeToken() helper the three call
        // sites now route every _lifetime.Token access through, once the manager has already
        // completed a real DisposeAsync() - the exact end-state the race exposes early - and assert
        // the exception it throws identifies this manager, not the CancellationTokenSource.
        [Fact]
        [Trait("Category", "Contract")]
        public async Task LifetimeToken_AfterDisposeAsync_ThrowsWithTheManagersIdentity_NotTheCancellationTokenSources()
        {
            var manager = CreateFixedManager(1, _ => Task.FromResult(1));
            await manager.WarmupAsync();
            await manager.DisposeAsync();

            var lifetimeTokenMethod = manager.GetType().GetMethod("LifetimeToken", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Method 'LifetimeToken' not found.");

            var thrown = Assert.Throws<TargetInvocationException>(() => lifetimeTokenMethod.Invoke(manager, null));
            var ex = Assert.IsType<ObjectDisposedException>(thrown.InnerException);
            Assert.DoesNotContain("CancellationTokenSource", ex.Message);
        }

        // ---------------------------------------------------------------------
        // 1.22b - An ordinary DisposeAsync() racing a still-blocked heartbeat callback must not
        // dispose the resource either. The guard "when (!_lifetime.IsCancellationRequested)"
        // correctly isolates a genuine pulse-budget timeout, but misses the case where _lifetime
        // itself cancelled the same linked pulseTimeout - an ordinary shutdown, not a timeout. That
        // case fell through to the generic catch, which disposed the resource immediately: the same
        // use-after-dispose race already fixed for the timeout path, reopened for the shutdown path.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAStillBlockedHeartbeatCallback_DoesNotDisposeTheResourceWhileStillInUse()
        {
            DisposableProbe? firstProbe = null;
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<DisposableProbe> builder = new RingBufferBuilder<DisposableProbe>("ContractDisposeRacesBlockedHeartbeat", null);
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
                    // A pulse budget of 5s means DisposeAsync() below - fired well inside that
                    // budget - is an ordinary shutdown, not a pulse-budget timeout.
                    releaseCallback.Wait(TimeSpan.FromSeconds(5));
                    value.Touch();
                    return true;
                }, pulse: TimeSpan.FromSeconds(5))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // pulse also governs the interval before the first heartbeat fires, so the callback
            // only starts around the 5s mark - wait comfortably past that.
            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(7)), "Expected the heartbeat callback to start.");

            // Dispose while the callback is still blocked, well inside its 5s pulse budget.
            await service.DisposeAsync();

            releaseCallback.Set();
            // Give the callback time to wake up and call Touch().
            await Task.Delay(300);

            Assert.NotNull(firstProbe);
            Assert.False(firstProbe!.TouchedAfterDispose, "Expected the resource to still be usable by the callback at the moment it touches it - an ordinary shutdown must not dispose it while the callback might still be using it.");
        }

        // ---------------------------------------------------------------------
        // 1.24 - A normal DisposeAsync racing an in-progress scale-up or heartbeat-triggered
        // replacement must not be logged as a factory TimeoutException: the outer
        // OperationCanceledException catches in CreateItemsAsync and CreateSingleReplacementAsync
        // must distinguish "the factory/overall deadline actually elapsed" from "DisposeAsync
        // cancelled the lifetime token while this was in flight" - conflating the two as an
        // identical timeout would mislead an on-call engineer into thinking the factory/broker was
        // unhealthy during an ordinary clean shutdown.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnInProgressScaleUp_DoesNotLogAFalseFactoryTimeout()
        {
            var errors = new List<Exception>();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringScaleUpNoFalseTimeout", null);
            var service = builder
                .Factory(async ct => { await Task.Delay(TimeSpan.FromSeconds(2), ct); return 1; }, TimeSpan.FromSeconds(5))
                .OnError(ex => errors.Add(ex))
                .ElasticCapacity(2, 5, 2, 1, TimeSpan.FromSeconds(30))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            // Give the engine time to dequeue the Switch command and actually start CreateItemsAsync
            // (the factory is mid-delay) before racing it with a normal dispose.
            await Task.Delay(200);
            await service.DisposeAsync();
            await Record.ExceptionAsync(() => switchTask);

            Assert.DoesNotContain(errors, ex => ex is TimeoutException te && te.Message.Contains("Timeout ScaleUp"));
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnInProgressHeartbeatReplacement_DoesNotLogAFalseFactoryTimeout()
        {
            var errors = new List<Exception>();
            var invalidated = new ManualResetEventSlim();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringReplacementNoFalseTimeout", null);
            var service = await builder
                .Factory(async ct =>
                {
                    // The very first 2 calls are the warmup fill - answer those immediately so the
                    // heartbeat has an item to work with; only the replacement (triggered below)
                    // needs to be slow enough to still be in flight when DisposeAsync races it.
                    if (!invalidated.IsSet)
                    {
                        return 1;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    return 1;
                }, TimeSpan.FromSeconds(5))
                .OnError(ex => errors.Add(ex))
                .HeartBeat(_ =>
                {
                    invalidated.Set();
                    return false;
                }, pulse: TimeSpan.FromMilliseconds(100))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(invalidated.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat to invalidate an item, triggering a replacement.");
            // Give the engine time to dequeue ReplaceOne and actually start CreateSingleReplacementAsync
            // (the factory is mid-delay) before racing it with a normal dispose.
            await Task.Delay(200);
            await service.DisposeAsync();

            Assert.DoesNotContain(errors, ex => ex is TimeoutException te && te.Message.Contains("Timeout factory (replacement)"));
        }

        // ---------------------------------------------------------------------
        // 1.25 - An ordinary DisposeAsync() racing an in-progress WarmupAsync() must not be logged
        // as "RingBuffer did not reach initial capacity": the warmup completion wait is bounded by
        // _lifetime.Token so a concurrent dispose does not hang it forever, but the catch that
        // observes that cancellation must not treat it identically to a genuine factory failure to
        // reach capacity - logging it as an ERROR during an ordinary clean shutdown would be
        // misleading, the same class of gap as the scale-up/replacement case above, in a different
        // code path.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnInProgressWarmup_DoesNotLogAFalseCapacityFailure()
        {
            var errors = new List<Exception>();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringWarmupNoFalseFailure", null);
            var service = builder
                .Factory(async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return 1; }, TimeSpan.FromSeconds(10))
                .OnError(ex => errors.Add(ex))
                .FixedCapacity(2)
                .Build();

            var warmupTask = service.WarmupAsync();
            // Give the engine time to dequeue Warmup and start CreateItemsAsync (the factory is
            // mid-delay) before racing it with a normal dispose.
            await Task.Delay(200);
            await service.DisposeAsync();
            await Record.ExceptionAsync(() => warmupTask);

            Assert.DoesNotContain(errors, ex => ex is InvalidOperationException ioe && ioe.Message.Contains("did not reach initial capacity"));
        }

        // ---------------------------------------------------------------------
        // 1.27 - A heartbeat tick's own internal acquire can race DisposeAsync() in a narrow window:
        // _disposed is already true, but _lifetime.Token has not yet observed cancellation.
        // AcquireCoreAsync's ObjectDisposedException.ThrowIf(_disposed, this) throws in that window,
        // and RunHeartbeatAsync's outer catch only caught OperationCanceledException - so the
        // ObjectDisposedException propagated out, faulted _heartbeatTask, and got logged as an
        // unexpected error, indistinguishable from a genuine fault. Same bug class as elsewhere in
        // this file: an ordinary shutdown miscategorized as a failure, just in a different location.
        // This race is narrow and timing-dependent, not reproducible on demand, so this test
        // reproduces it probabilistically over many iterations with a very small PulseHeartBeat to
        // maximize the hit rate.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingTheHeartbeatsOwnFirstAcquire_NeverLogsAnObjectDisposedException()
        {
            var errors = new List<Exception>();
            for (var i = 0; i < 300; i++)
            {
                IRingBufferBuilder<int> builder = new RingBufferBuilder<int>($"ContractHeartbeatAcquireDisposeRace{i}", null);
                var service = await builder
                    .Factory(_ => Task.FromResult(1))
                    .OnError(ex => errors.Add(ex))
                    .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(1))
                    .FixedCapacity(2)
                    .BuildWarmupAsync();

                // Timed to land near the first pulse's elapse, not immediately after warmup - the
                // race needs the heartbeat loop past its Task.Delay(pulse) and heading into its own
                // acquire, not still inside the delay (where an ordinary OperationCanceledException
                // is already handled cleanly).
                await Task.Delay(1);
                await service.DisposeAsync();
            }

            Assert.DoesNotContain(errors, ex => ex is ObjectDisposedException);
        }

        // ---------------------------------------------------------------------
        // 1.27b - A residual instance of the same shutdown-vs-failure ambiguity found while
        // verifying the fix above: if a fast heartbeat callback finishes at nearly the same instant
        // an ordinary DisposeAsync() cancels _lifetime, the completion-check guard
        // ("!heartbeatWork.IsCompleted") can evaluate false even though this was just an ordinary
        // shutdown. The exception then fell through to the generic catch, which logged the
        // resulting OperationCanceledException/TaskCanceledException as an unconditional error.
        // Disposing the resource in that fallthrough was still safe - the callback had genuinely
        // already finished - only the log level was wrong.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAFastHeartbeatCallbackThatFinishesAtTheSameInstant_NeverLogsItAsAnError()
        {
            var errors = new List<Exception>();
            for (var i = 0; i < 300; i++)
            {
                IRingBufferBuilder<int> builder = new RingBufferBuilder<int>($"ContractHeartbeatFastFinishDisposeRace{i}", null);
                var service = await builder
                    .Factory(_ => Task.FromResult(1))
                    .OnError(ex => errors.Add(ex))
                    .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(1))
                    .FixedCapacity(2)
                    .BuildWarmupAsync();

                // Timed to land near the first pulse's elapse, same as the ObjectDisposedException
                // race above - needed here too, since the callback only runs (and only has a
                // chance to race its own completion against _lifetime cancelling) once the loop is
                // past its initial Task.Delay(pulse).
                await Task.Delay(1);
                await service.DisposeAsync();
            }

            Assert.DoesNotContain(errors, ex => ex is OperationCanceledException);
        }

        // ---------------------------------------------------------------------
        // 1.28 - DisposeAsync() must actually wait (bounded by PulseHeartBeat) for an orphaned
        // heartbeat callback's deferred dispose to finish, not merely schedule it and return: the
        // deferred continuation must not be fire-and-forget, left out of DisposeAsync's own
        // Task.WhenAll(pending) - the pooled resource could otherwise still be undisposed by the
        // time DisposeAsync() returned, a real leak if the host process exits shortly after.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WaitsForAnOrphanedHeartbeatCallbacksDeferredDispose_BeforeReturning()
        {
            DisposableProbe? firstProbe = null;
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<DisposableProbe> builder = new RingBufferBuilder<DisposableProbe>("ContractDisposeWaitsForDeferredHeartbeatDispose", null);
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
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    return true;
                }, pulse: TimeSpan.FromMilliseconds(500))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 500ms pulse timeout actually fire while the callback is still blocked, so
            // the deferred-dispose continuation gets enqueued.
            await Task.Delay(700);

            // Release the callback shortly after DisposeAsync() starts waiting - well within the
            // PulseHeartBeat grace period DisposeAsync now allows for the deferred dispose.
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                releaseCallback.Set();
            });

            await service.DisposeAsync();

            Assert.NotNull(firstProbe);
            Assert.Equal(1, firstProbe!.DisposeCount);
        }

        // ---------------------------------------------------------------------
        // 1.29 - DisposeAsync()'s idle-item drain must actually be unconditional: a raw loop
        // calling DisposeItemAsync directly for each idle item must not abort on the first one
        // whose Dispose() throws, leaking every remaining item plus _lifetime/_meter/
        // _activitySource permanently (since _disposeGuard makes a second DisposeAsync() call a
        // silent no-op) - the same failure mode already fixed once for the warmup-exception
        // trigger, here via a different trigger (an item's own Dispose() failing instead). Fixed
        // by routing through the existing DisposeItemsDefensivelyAsync helper (already used by
        // RemoveItemsAsync), which disposes every item regardless of any individual failure and
        // logs instead of propagating.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenAPooledItemsDisposeThrows_StillDisposesEveryOtherItem()
        {
            var probes = new List<ThrowingOnDisposeProbe>();
            var index = 0;
            var errors = new List<Exception>();

            IRingBufferBuilder<ThrowingOnDisposeProbe> builder = new RingBufferBuilder<ThrowingOnDisposeProbe>("ContractDisposeThrowingItemStillDisposesRest", null);
            var service = await builder
                .Factory(_ =>
                {
                    var i = Interlocked.Increment(ref index);
                    var probe = new ThrowingOnDisposeProbe(throwOnDispose: i == 2);
                    lock (probes) probes.Add(probe);
                    return Task.FromResult(probe);
                })
                .OnError(ex => errors.Add(ex))
                .FixedCapacity(3)
                .BuildWarmupAsync();

            await service.DisposeAsync();

            Assert.Equal(3, probes.Count);
            Assert.All(probes, p => Assert.True(p.Disposed, "Expected every idle item to be disposed, even though one of them threw."));
            Assert.Contains(errors, ex => ex is InvalidOperationException);
        }

        // ---------------------------------------------------------------------
        // 1.30 - DisposeAsync's grace-period-timeout warning (an orphaned heartbeat callback's
        // deferred disposal did not finish within PulseHeartBeat) must actually reach the
        // configured Logger. Originally written against BackgroundLogger(true)'s own queue-
        // completion-ordering bug; that queue no
        // longer exists (ADR007V03 removed BackgroundLogger entirely - logging is always
        // synchronous now), so this is a plain regression test for the message itself.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenAnOrphanedHeartbeatDisposalOutlivesTheGracePeriod_LogsAWarning()
        {
            var logger = new CapturingLogger();
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeGracePeriodWarning", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(logger)
                .HeartBeat(_ =>
                {
                    callbackStarted.Set();
                    // Never actually released within the test - stays blocked well past
                    // DisposeAsync's own PulseHeartBeat-bounded grace period for the deferred
                    // dispose, forcing the grace-period-timeout LogMessage this test is about.
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    return true;
                }, pulse: TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 300ms pulse timeout fire while the callback is still blocked, enqueueing the
            // deferred-dispose continuation.
            await Task.Delay(500);

            await service.DisposeAsync();

            Assert.Contains(logger.Messages, m => m.Contains("did not wait for"));
        }

        // ---------------------------------------------------------------------
        // 1.39-1.40 - A pooled item's own Dispose()/DisposeAsync() had no bound anywhere - unlike
        // Factory (FactoryTimeout) and the heartbeat callback (PulseHeartBeat). First gap: a hang
        // in DisposeAsync()'s own idle-item drain loop just delayed/blocked shutdown itself.
        // Second, far worse: RemoveItemsAsync runs on the single-consumer engine's own thread
        // during a scale-down, so a hang there stalled every other command forever, including the
        // wait DisposeAsync() itself has on _engineTask. Both fixed by bounding each item's dispose
        // wait to PulseHeartBeat (the same grace-period precedent already established for the
        // heartbeat callback) inside the shared DisposeItemsDefensivelyAsync helper.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenAnIdleItemsDisposeHangsForever_StillReturnsWithinTheGracePeriod()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractDrainLoopHangingDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingDisposeProbe(releaseHang)))
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var sw = Stopwatch.StartNew();
            var disposeTask = service.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            sw.Stop();

            Assert.Same(disposeTask, completed);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected DisposeAsync() to return within the grace period, took {sw.Elapsed}.");

            releaseHang.Set();
        }

        // ---------------------------------------------------------------------
        // DisposeItemsDefensivelyAsync's grace period
        // (PulseHeartBeat) only ever bounded IAsyncDisposable.DisposeAsync() - a plain synchronous
        // IDisposable.Dispose() blocks the calling thread before WaitAsync gets a chance to apply
        // any bound at all. HangingDisposeProbe above is IAsyncDisposable and already dispatches to
        // a background thread internally, so it never actually exercised this gap.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenAnIdleItemsSynchronousDisposeHangsForever_StillReturnsWithinTheGracePeriod()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingSyncDisposeProbe> builder = new RingBufferBuilder<HangingSyncDisposeProbe>("ContractDrainLoopHangingSyncDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingSyncDisposeProbe(releaseHang)))
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var sw = Stopwatch.StartNew();
            var disposeTask = service.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            sw.Stop();

            Assert.Same(disposeTask, completed);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected DisposeAsync() to return within the grace period, took {sw.Elapsed}.");

            releaseHang.Set();
        }

        // ---------------------------------------------------------------------
        // The batch of idle items must not be disposed sequentially - N hung
        // items would otherwise cost N x PulseHeartBeat in total, instead of one bounded wait for
        // the whole batch.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenMultipleIdleItemsAllHangOnDispose_TheWholeBatchIsBoundedByOnePulseHeartBeat_NotN()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingSyncDisposeProbe> builder = new RingBufferBuilder<HangingSyncDisposeProbe>("ContractDrainLoopBatchHangingSyncDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingSyncDisposeProbe(releaseHang)))
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(1000))
                .FixedCapacity(4)
                .BuildWarmupAsync();

            var sw = Stopwatch.StartNew();
            var disposeTask = service.DisposeAsync().AsTask();
            // 4 hung items sequentially would cost ~4 x 1000ms = 4000ms just for the grace periods,
            // on top of real dispatch overhead - budget well below that to prove concurrency, but
            // above one grace period (1000ms) plus enough scheduling slack for shared CI runners.
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(2500)));
            sw.Stop();

            Assert.Same(disposeTask, completed);
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(2500), $"Expected the batch to be bounded by ~one PulseHeartBeat, took {sw.Elapsed}.");

            releaseHang.Set();
        }
    }
}
