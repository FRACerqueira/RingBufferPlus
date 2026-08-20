// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Behavioral contract tests for the v4 -> v5 concurrency rewrite (see
// doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md).
//
// This suite started as a set of tests describing the INTENDED v5 behavior, with every
// regression case marked [Fact(Skip = "...")] because it failed against the v4 implementation.
// The Channel-based, single-consumer engine (ADR001) later replaced RingBufferManager<T>, so
// every test below is now ported to the v5 API and unskipped - this file is the rewrite's
// acceptance gate, and it is green.
//
// Notably, 1.3 (concurrent SwitchToAsync) is no longer "best-effort": because the new engine is a
// single sequential consumer, "exactly one accepted caller" is now a deterministic guarantee, not
// a probabilistic reproduction of a race. That upgrade from probabilistic to deterministic is
// itself evidence the rewrite fixed the race by construction, per ADR001.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public class RingBufferContractTests
    {
        private static RingBufferManager<int> CreateFixedManager(int capacity, Func<CancellationToken, Task<int>> factory, CancellationToken lifetime = default)
        {
            return new RingBufferManager<int>(lifetime)
            {
                Name = "ContractBuffer",
                Capacity = capacity,
                MinCapacity = capacity,
                MaxCapacity = capacity,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(5),
                SamplesBase = TimeSpan.FromSeconds(5),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(300),
                Factory = factory
            };
        }

        private static object GetPrivateField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {instance.GetType()}.");
            return field.GetValue(instance)!;
        }

        // ---------------------------------------------------------------------
        // 1.7 - Baseline contracts.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AcquireTimeout_ReturnsUnsuccessful_WhenBufferIsExhausted()
        {
            // Arrange: fixed capacity of 2, both items held (never disposed back).
            var manager = CreateFixedManager(2, _ => Task.FromResult(1));
            await manager.WarmupAsync();
            _ = await manager.AcquireAsync();
            _ = await manager.AcquireAsync();

            // Act
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await manager.AcquireAsync();
            sw.Stop();

            // Assert: unsuccessful, and bounded by AcquireTimeout with a generous tolerance
            // (this is a wall-clock assertion; keep the tolerance wide to avoid CI flakiness).
            Assert.False(result.Successful);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"AcquireAsync took {sw.Elapsed}, expected close to the 300ms AcquireTimeout.");

            await manager.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ReturnsFalse_WhenAlreadyAtRequestedTarget()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractNoOpSwitch", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();
            Assert.True(service.IsInitCapacity);

            // Act: already at InitCapacity, asking to switch to InitCapacity again must be a no-op.
            var result = await service.SwitchToAsync(ScaleSwitch.InitCapacity);

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
                .ElasticCapacity(3, 2, 6, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            // Act
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

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
        // 1.1 - Concurrent warmup idempotency
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ConcurrentWarmup_InvokesFactoryExactlyCapacityTimes()
        {
            // Arrange
            var factoryCalls = 0;
            var manager = CreateFixedManager(5, _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(1);
            });

            // Act: many concurrent callers. Warmup is a Lazy<Task> now, so idempotency is
            // guaranteed by construction, not by a hand-rolled flag check - no need to force
            // the race deterministically anymore, the property holds regardless of scheduling.
            // Task.Run forces genuine thread-pool parallelism (a bare Task.WhenAll over the
            // async calls directly could run sequentially on the calling thread otherwise).
            var warmups = Enumerable.Range(0, 20).Select(_ => Task.Run(() => manager.WarmupAsync()));
            await Task.WhenAll(warmups);

            // Assert: the factory must be invoked exactly Capacity times, never duplicated.
            Assert.Equal(5, factoryCalls);

            await manager.DisposeAsync();
        }

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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromMilliseconds(300))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            blockFactory = true;
            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
                .ElasticCapacity(4, 2, 12, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            // Act: many concurrent callers racing to request the same scale target. The engine is a
            // single sequential consumer, so this is now a deterministic guarantee, not a race
            // reproduction - see the file header note.
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity))));

            // Assert: exactly one caller wins the race and gets the request accepted.
            Assert.Equal(1, results.Count(accepted => accepted));

            await service.DisposeAsync();
        }

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
                .HeartBeat(_ => { })
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
            foreach (var fieldName in new[] { "_engineTask", "_heartbeatTask", "_loggerTask", "_sampleTickTask" })
            {
                if (GetPrivateField(service, fieldName) is Task task)
                {
                    Assert.False(task.IsFaulted, $"{fieldName} faulted: {task.Exception}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // 1.4b - Disposing while a warmup is still in flight must not leak a faulted,
        // unawaited background task (advisor review: DisposeAsync used to snapshot which
        // pumps to await before WarmupCoreAsync had finished assigning them).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_DuringInFlightWarmup_DoesNotLeakFaultedBackgroundTasks()
        {
            // Arrange: a slow factory widens the window for DisposeAsync to race the warmup.
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringWarmup", null);
            var service = builder
                .Factory(async _ => { await Task.Delay(50); return 1; })
                .HeartBeat(_ => { })
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
            foreach (var fieldName in new[] { "_engineTask", "_heartbeatTask", "_loggerTask", "_sampleTickTask" })
            {
                if (GetPrivateField(service, fieldName) is Task task)
                {
                    Assert.False(task.IsFaulted, $"{fieldName} faulted: {task.Exception}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // 1.4c - AcquireAsync after disposal must throw a clean ObjectDisposedException,
        // not an accidental one from CancellationTokenSource.CreateLinkedTokenSource
        // observing an already-disposed _lifetime (advisor review).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AcquireAsync_ThrowsObjectDisposed_AfterDisposeAsync()
        {
            // Arrange
            var manager = CreateFixedManager(2, _ => Task.FromResult(1));
            await manager.WarmupAsync();
            await manager.DisposeAsync();

            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.AcquireAsync().AsTask());
        }

        // ---------------------------------------------------------------------
        // 1.4d - Invalidate() replaces the item via the engine, keeping capacity stable
        // (advisor review: the ReplaceOne path had zero test coverage).
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
                .ElasticCapacity(4, 2, 8)
                .Build();
            await service.WarmupAsync();
            await service.DisposeAsync();

            // Act & Assert: using the manual-switch mechanism after disposal must throw loudly,
            // not silently no-op (there is no longer a separate scale queue to leak - the whole
            // engine loop is gone - but the public contract must still reject post-disposal work).
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity));
        }

        // ---------------------------------------------------------------------
        // 1.6 - WarmupRingBufferAsync honors its token and throws when the buffer is missing
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupRingBufferAsync_HonorsTheProvidedToken()
        {
            // Arrange
            var ringBufferServiceMock = new Mock<IRingBufferService<int>>();
            ringBufferServiceMock.Setup(x => x.Name).Returns("testBuffer");
            ringBufferServiceMock.Setup(x => x.WarmupAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(ringBufferServiceMock.Object);
            var serviceProvider = services.BuildServiceProvider();

            var hostMock = new Mock<IHost>();
            hostMock.Setup(x => x.Services).Returns(serviceProvider);

            using var explicitToken = new CancellationTokenSource();

            // Act
            await hostMock.Object.WarmupRingBufferAsync<int>("testBuffer", explicitToken.Token);

            // Assert: the explicitly provided token, not ApplicationStopping/None, must reach WarmupAsync.
            ringBufferServiceMock.Verify(x => x.WarmupAsync(explicitToken.Token), Times.Once);
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupRingBufferAsync_ThrowsWhenBufferIsNotRegistered()
        {
            // Arrange: no IRingBufferService<int> registered at all.
            var services = new ServiceCollection();
            var serviceProvider = services.BuildServiceProvider();

            var hostMock = new Mock<IHost>();
            hostMock.Setup(x => x.Services).Returns(serviceProvider);

            // Act + Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                hostMock.Object.WarmupRingBufferAsync<int>("missingBuffer"));
        }

        // ---------------------------------------------------------------------
        // 1.8 - A factory (or a user item's Dispose) that throws a non-cancellation exception
        // must not kill the engine loop. Before this fix, every catch from Factory up to the
        // engine's command loop filtered exclusively on OperationCanceledException, so a plain
        // exception faulted _engineTask permanently and every public async method (Warmup/
        // Acquire/Switch/Dispose) could then hang forever - see TODO/relatorio-viabilidade-
        // ringbufferplus-v5.md, finding F1/R1.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_WhenFactoryThrows_PropagatesTheRealException_AndDoesNotHang()
        {
            // Arrange: every factory call throws a plain (non-cancellation) exception.
            var manager = CreateFixedManager(3, _ => throw new InvalidOperationException("boom"));

            // Act: bound the wait - before the fix, the dead engine never resolved this TCS and
            // WarmupAsync hung forever.
            var warmupTask = manager.WarmupAsync();
            var completed = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(3)));

            // Assert: must complete (not hang) and surface the real exception, not a generic wrapper.
            Assert.Same(warmupTask, completed);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => warmupTask);
            Assert.Equal("boom", ex.Message);

            await manager.DisposeAsync();
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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;

            // Act: the scale-up's factory calls throw. Before the fix, this faulted the engine
            // permanently and every call below would then hang instead of completing.
            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
        public async Task AutoScaleAcquireFault_WhenTriggeredScaleUpFactoryThrows_EngineSurvives_AndAutoscaleRecovers()
        {
            // Arrange: init capacity 4, min 2 (init != min, to avoid the separate, already-known R4
            // defect where the scale-up target formula picks a no-op when init == min), autoscale on
            // the first fault, factory throws only while "throwing" is true.
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFaultTriggeredScaleThrows", null);
            var service = builder
                .Factory(_ => throwing ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(4, 2, 6, 1, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault(0)
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            // Exhaust the pool so the next acquire times out and posts a Fault command.
            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            var held3 = await service.AcquireAsync();
            var held4 = await service.AcquireAsync();

            throwing = true;
            var faultedTask = service.AcquireAsync().AsTask();
            var faultedCompleted = await Task.WhenAny(faultedTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(faultedTask, faultedCompleted);
            Assert.False((await faultedTask).Successful);

            // Give the engine a moment to process the Fault-triggered scale-up (posted fire-and-forget).
            await Task.Delay(300);

            // Assert: capacity did not move (the scale-up's factory call failed), but the engine is
            // still alive - before the fix, this permanently killed the engine and disabled autoscale.
            Assert.True(service.IsInitCapacity);

            // Recover the factory and force another fault: autoscale must still work.
            throwing = false;
            var faulted2Task = service.AcquireAsync().AsTask();
            var faulted2Completed = await Task.WhenAny(faulted2Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(faulted2Task, faulted2Completed);
            Assert.False((await faulted2Task).Successful);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity);

            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await held3.DisposeAsync();
            await held4.DisposeAsync();

            var disposeTask = service.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(disposeTask, disposeCompleted);
            await disposeTask;
        }

        // ---------------------------------------------------------------------
        // 1.9 - A command whose completion signal races DisposeAsync's cancellation must not hang
        // forever if cancellation wins. Before this fix, WarmupCoreAsync awaited its engine
        // TaskCompletionSource with no cancellation token, so when the Warmup command lost that race
        // (abandoned unread in the channel), nothing could ever unblock it - and DisposeAsync itself
        // awaits that same signal, so disposal hung too. This is a genuine data race inside
        // Channel<T> (which of "data arrived" vs "cancellation requested" the channel's pending wait
        // observes first) - not forceable to a single deterministic outcome from outside the channel,
        // so this is a bounded stress loop, not a one-shot repro (CLAUDE.md rule 5 step 3; empirically
        // ~97% hang rate per iteration against the unfixed code during triage). See TODO/relatorio-
        // viabilidade-ringbufferplus-v5.md, finding F4.
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
        // 1.10 - RingBufferManager.DisposeAsync's own _disposed guard has the same non-atomic
        // check-then-set shape as RingBufferValue's (finding F2, fixed via the identical
        // Interlocked.Exchange idiom just above). Two separate attempts to reproduce it as a live
        // race - the original audit probe (300 concurrent-dispose attempts) and a follow-up with
        // tightly-synchronized dedicated threads (1000 attempts, ~12 minutes) - both produced zero
        // hits, so this is fixed on the strength of the proven-necessary pattern rather than a
        // red/green regression test (CLAUDE.md rule 5 step 3: the mechanism is named, but a test
        // that costs 12 minutes per run for no observed signal does not earn a place in the suite).
        // See TODO/relatorio-viabilidade-ringbufferplus-v5.md, finding F10, and TODO/plano-de-
        // acao.md P0#3 for the full account.
        // ---------------------------------------------------------------------

        // ---------------------------------------------------------------------
        // 1.11 - A HeartBeat callback that blocks past its pulse budget must not stop the heartbeat
        // pump forever, and the item it was holding must not be lost. Before this fix, the
        // CancellationToken passed to Task.Run only prevented the delegate from starting - it did
        // not cancel it once running - so the intended timeout guard was unreachable code. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, finding F3/R3.
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
                }, TimeSpan.FromMilliseconds(50))
                .FixedCapacity(2)
                .Build();
            try
            {
                await service.WarmupAsync();

                // With the fix, a second (and third, etc.) pulse must happen within a couple of
                // pulse budgets even though the first invocation is still blocked. Without the fix,
                // this never happens - the pump is dead until (if ever) the callback returns.
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
        // 1.12 - Disposal must always drain and clean up regardless of how a background pump
        // ended. Before this fix, Task.WhenAll(pending) only caught OperationCanceledException, so
        // any other fault (e.g. the ObjectDisposedException race below) escaped DisposeAsync before
        // draining pooled items and disposing _lifetime/_meter/_activitySource. See TODO/relatorio-
        // viabilidade-ringbufferplus-v5.md, finding F3/R2.
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
                    .HeartBeat(_ => { }, TimeSpan.FromMilliseconds(15))
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

        // ---------------------------------------------------------------------
        // 1.13 - An item returned via TurnbackAsync after the manager is already disposed must be
        // disposed itself, not silently dropped. Before this fix, the ChannelClosedException handler
        // had a bare "ignore" comment and never called DisposeItemAsync. See TODO/relatorio-
        // viabilidade-ringbufferplus-v5.md, finding F5/R12.
        // ---------------------------------------------------------------------

        private sealed class DisposableProbe : IDisposable
        {
            private int _disposeCount;
            public int DisposeCount => Volatile.Read(ref _disposeCount);
            public void Dispose() => Interlocked.Increment(ref _disposeCount);
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

        // ---------------------------------------------------------------------
        // 1.14 - The scale-up deadline must scale with the work requested (quantity * FactoryTimeout),
        // not with the sampling cadence (SamplesBase) - and a scale-up that still can't finish in
        // time must keep whatever capacity it already gained instead of discarding it. See TODO/
        // relatorio-viabilidade-ringbufferplus-v5.md, finding R5.
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
                .ElasticCapacity(2, 2, 5, 1, TimeSpan.FromMilliseconds(300))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

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
                .ElasticCapacity(2, 2, 5, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Partial progress is not surfaced as an exception - only "nothing at all was gained" is.
            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
        // 1.15 - Autoscale-on-fault must never be permanently disabled by a legal configuration.
        // Before this fix, initialCapacity == minCapacity made the scale-up target formula pick a
        // no-op (target == current) on every single fault, regardless of how many times it fired or
        // how healthy the factory was. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, finding R4.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AutoScaleAcquireFault_WhenInitialCapacityEqualsMinCapacity_StillScalesUp()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractAutoScaleInitEqualsMin", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault(0)
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();

            var faulted = await service.AcquireAsync();
            Assert.False(faulted.Successful);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (service.IsInitCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsInitCapacity, "Expected the acquire fault to trigger a scale-up away from the initial (== minimum) capacity.");

            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.16 - The documented autoscale fault threshold ("Default is 1 (after first fault)") must
        // match the implementation. Before this fix, the comparison used `>` instead of `>=`, so the
        // default numberOfFaults=1 actually required a second fault before scaling up - see TODO/
        // relatorio-viabilidade-ringbufferplus-v5.md, finding U-07 / P2 Decision C.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AutoScaleAcquireFault_WithDefaultThreshold_ScalesUpAfterExactlyOneFault()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractAutoScaleThresholdOffByOne", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(4, 2, 6, 1, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault() // default numberOfFaults = 1
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Exactly one fault - the documented "Default is 1 (after first fault)" must trigger
            // scale-up right here, not require a second one.
            var faulted = await service.AcquireAsync();
            Assert.False(faulted.Successful);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (service.IsInitCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsInitCapacity, "Expected a single acquire fault (the default numberOfFaults=1) to trigger scale-up immediately, not require a second fault.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }
    }
}
