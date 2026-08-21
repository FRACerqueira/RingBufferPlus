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

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // ---------------------------------------------------------------------
        // 1.36-1.38 - R23/R24/R25 (Round 7, Resiliência): a factory can throw
        // OperationCanceledException/TaskCanceledException for its own unrelated reasons (an
        // HttpClient/gRPC/DB driver's own internal timeout, nothing to do with this buffer's own
        // _lifetime) - before these fixes, every catch below misclassified that as an ordinary
        // shutdown, discarding the real exception. This completes (does not contradict) the
        // Round 6-confirmed "surface the real factory exception on a zero-progress batch" contract
        // - it just extends that contract to cover the case where the real exception happens to be
        // OperationCanceledException-shaped.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_WhenFactoryThrowsOperationCanceledException_PropagatesTheRealException_NotAGenericWrapper()
        {
            var manager = CreateFixedManager(3, _ => throw new TaskCanceledException("factory's own unrelated timeout"));

            var warmupTask = manager.WarmupAsync();
            var completed = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.Same(warmupTask, completed);
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => warmupTask);
            Assert.Equal("factory's own unrelated timeout", ex.Message);

            await manager.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenFactoryThrowsOperationCanceledExceptionDuringScaleUp_PropagatesRealException_NotASilentFalse()
        {
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFactoryThrowsOceDuringScale", null);
            var service = builder
                .Factory(_ => throwing ? throw new TaskCanceledException("factory's own unrelated timeout") : Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartbeatTriggeredReplacement_WhenFactoryThrowsOperationCanceledException_LogsTheRealException_NotAFabricatedTimeout()
        {
            var errors = new List<Exception>();
            var replacementShouldThrow = false;

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractReplacementFactoryThrowsOce", null);
            var service = await builder
                .Factory(_ => replacementShouldThrow ? throw new TaskCanceledException("factory's own unrelated timeout") : Task.FromResult(1))
                .OnError((_, ex) => { lock (errors) errors.Add(ex); })
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
            public bool TouchedAfterDispose { get; private set; }
            public void Dispose() => Interlocked.Increment(ref _disposeCount);
            public void Touch()
            {
                if (DisposeCount > 0)
                {
                    TouchedAfterDispose = true;
                }
            }
        }

        private sealed class ThrowingOnDisposeProbe : IDisposable
        {
            private readonly bool _throwOnDispose;
            public ThrowingOnDisposeProbe(bool throwOnDispose) => _throwOnDispose = throwOnDispose;
            public bool Disposed { get; private set; }
            public void Dispose()
            {
                Disposed = true;
                if (_throwOnDispose)
                {
                    throw new InvalidOperationException("Simulated pooled item Dispose() failure.");
                }
            }
        }

        private sealed class HangingDisposeProbe(ManualResetEventSlim release) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                // Capped at 10s so a broken fix can't actually hang the test process forever - the
                // assertions themselves are what prove the library-level bound (PulseHeartBeat) works.
                await Task.Run(() => release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Messages { get; } = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (Messages) Messages.Add(message);
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
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

        // ---------------------------------------------------------------------
        // 1.17 - A failed WarmupAsync() must not permanently brick the instance (ADR011, P2
        // Decision B). Before this fix, a failed warmup was cached forever by the underlying
        // Lazy<Task>, and the only way to recover was constructing a brand new instance - a real
        // problem given every DI guide recommends registering the buffer as a singleton. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, U-22.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_AfterAFailedAttempt_RetriesInsteadOfCachingTheFailureForever()
        {
            var shouldFail = true;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractWarmupRetry", null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .FixedCapacity(2)
                .Build();

            var firstEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.NotNull(firstEx);

            // Retry: an explicit second WarmupAsync() call must attempt from scratch, not rethrow
            // the same cached failure. The factory recovers before the retry.
            shouldFail = false;
            var secondEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.Null(secondEx);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            await acquired.DisposeAsync();

            await service.DisposeAsync();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AcquireAsync_AfterAFailedWarmup_DoesNotAutoRetry_UntilWarmupAsyncIsCalledAgain()
        {
            var shouldFail = true;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractWarmupNoAutoRetry", null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .FixedCapacity(2)
                .Build();

            var firstEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.NotNull(firstEx);

            // The factory recovers, but nobody called WarmupAsync() again - AcquireAsync must keep
            // observing the failed attempt, not silently retry warmup on its own.
            shouldFail = false;
            var acquireEx = await Record.ExceptionAsync(() => service.AcquireAsync().AsTask());
            Assert.NotNull(acquireEx);

            // An explicit retry must now succeed.
            var retryEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.Null(retryEx);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            await acquired.DisposeAsync();

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.18 - A scale-up in progress must not let sample ticks pile up and drain in a burst
        // right after it finishes (F6, P3). Before this fix, the dead `_scaling` guard in
        // ProcessTickAsync never actually ran (the engine is a single serial consumer, so by the
        // time a queued Tick is processed the scale is always already done), while
        // RunSampleTickAsync kept enqueueing Ticks every cadence regardless - during a slow
        // scale-up those Ticks pile up in the channel and get drained back-to-back the instant the
        // engine frees up, producing several near-duplicate samples of the post-scale-up idle
        // count and triggering an immediate scale-down evaluation instead of a properly
        // time-spread one. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, F6.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_FollowedByEligibleScaleDown_DoesNotEvaluateScaleDownImmediatelyAfter()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleUpThenScaleDownTiming", null);
            var service = builder
                .Factory(_ => Task.Delay(150).ContinueWith(_ => 1))
                .ElasticCapacity(3, 2, 10, 5, TimeSpan.FromSeconds(1))
                .AutoScaleAcquireFault(0)
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - this acquire times out and enqueues the autoscale Fault, which will
            // scale 3 -> 10 (ScaleDownMax ends up 10-3+2=9, so a fully idle pool afterwards, with
            // all 10 items idle, is eligible for an immediate scale-down back down to 3).
            var faulted = await service.AcquireAsync();
            Assert.False(faulted.Successful);

            // Release the originally held items now, so once the scale-up finishes every item
            // (originals + newly created) is idle - the scale-down-eligible condition.
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the fault-triggered scale-up to reach max capacity.");
            var scaleUpCompletedAt = DateTime.UtcNow;

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
        // 1.19 - The autoscale fault counter must reset once its threshold is reached even while
        // pinned at MaxCapacity (F7, P3), so it does not pile up unboundedly and cause a single
        // fault after a later scale-down to immediately re-trigger a scale back to MaxCapacity
        // instead of requiring a fresh batch of NumberFault faults. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, F7/R7.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AutoScaleAcquireFault_FaultsWhilePinnedAtMaxCapacity_DoNotCauseFlappingAfterScaleDown()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFaultCounterFlapping", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(3, 2, 8, 5, TimeSpan.FromMilliseconds(500))
                .AutoScaleAcquireFault(3)
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            // Phase 1: 3 faults (NumberFault=3) with an empty pool scale 3 -> 8 (max).
            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }
            for (var i = 0; i < 3; i++)
            {
                var faulted = await service.AcquireAsync();
                Assert.False(faulted.Successful);
            }
            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the initial batch of 3 faults to scale up to max capacity.");

            // Phase 2: while pinned at max, acquire everything (3 originals + 5 new = 8) and
            // generate 2 full extra batches of faults (6 faults) - nothing to scale up to, but a
            // correct implementation must still forget these batches instead of letting the
            // counter pile up unboundedly.
            for (var i = 0; i < 5; i++)
            {
                held.Add(await service.AcquireAsync());
            }
            Assert.Equal(8, held.Count);
            for (var i = 0; i < 6; i++)
            {
                var faulted = await service.AcquireAsync();
                Assert.False(faulted.Successful);
            }

            // Phase 3: release everything and let the natural idle-triggered scale-down bring
            // capacity back down from max.
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            held.Clear();
            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.IsMaxCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsMaxCapacity, "Expected the idle pool to scale back down from max capacity.");

            // Phase 4: exactly 2 fresh faults - one short of the fresh NumberFault=3 threshold.
            // With the counter correctly forgotten in phase 2, this must NOT be enough to
            // re-trigger a scale back to max. Under the bug (never reset while pinned at max),
            // the stale leftover count from phase 2 plus these 2 fresh faults crosses the
            // threshold again, causing flapping.
            // Not asserting success/failure on these individual acquires: under the bug, a
            // premature scale-up may race with this very loop and hand back a freshly created
            // item - the final capacity check below is the authoritative signal either way.
            var currentCapacity = service.CurrentCapacity;
            for (var i = 0; i < currentCapacity + 2; i++)
            {
                var attempt = await service.AcquireAsync();
                if (attempt.Successful)
                {
                    held.Add(attempt);
                }
            }
            await Task.Delay(500);
            Assert.False(service.IsMaxCapacity, "Expected 2 fresh faults (one short of the NumberFault=3 threshold) to NOT re-trigger a scale back to max capacity - the fault counter must have been forgotten after the earlier batches at max capacity, not accumulated unboundedly.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.20 - A scale-down must not block the engine's single-consumer loop waiting for busy
        // items to be returned (R6, P3): it must only take whatever is already idle right now
        // (opportunistic, partial-if-needed), the same "keep partial progress" spirit already
        // applied to scale-up (R5/P1#8), instead of blocking every other command (Fault, another
        // Switch, ReplaceOne) behind a wait bounded by SamplesBase. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, R6.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_WithNotEnoughIdleItems_DoesNotBlockTheEngineForOtherCommands()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractScaleDownDoesNotBlockEngine", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(5, 2, 5, 5, TimeSpan.FromSeconds(2))
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
            var firstSwitchAccepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity);
            Assert.True(firstSwitchAccepted);

            // A second, independent command posted right after must not be stuck behind the
            // first one's engine-side processing. Under the bug, the engine blocks inside the
            // first scale-down waiting for a 3rd item to free up, so this second command's own
            // Accepted signal (resolved only once the engine reaches it) is delayed by roughly
            // the first operation's full SamplesBase-bound wait/timeout (2s here).
            var sw = Stopwatch.StartNew();
            await service.SwitchToAsync(ScaleSwitch.InitCapacity);
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Expected the engine to process the second command promptly instead of being stuck behind the first scale-down's wait for busy items - took {sw.Elapsed}.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.21 - The heartbeat pump's own internal AcquireAsync call must not count toward the
        // autoscale fault budget (R11, P3): it is an internal health check, not consumer demand,
        // and letting its timeout trigger a scale-up is a self-inflicted false signal. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, R11.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task HeartBeat_AcquireTimeoutWhilePoolIsExhausted_DoesNotCountTowardAutoScaleFaultBudget()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractHeartbeatDoesNotFault", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .HeartBeat(_ => { }, TimeSpan.FromMilliseconds(100))
                .ElasticCapacity(2, 2, 4, 5, TimeSpan.FromSeconds(5))
                .AutoScaleAcquireFault(0)
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
        // resource disposed out from under it (F12, Rodada 2): the orphaned callback keeps running
        // on its own thread-pool thread and may still be touching the resource when the timeout
        // fires. Before this fix, Invalidate() + the enclosing "await using" disposed the resource
        // synchronously on timeout, while the callback could still be using it - a genuine
        // use-after-dispose race on the caller's own object (a DB connection, a RabbitMQ channel),
        // not just internal bookkeeping. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, F12.
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
                    if (Interlocked.CompareExchange(ref firstProbe, value.Current, null) is not null
                        && !ReferenceEquals(firstProbe, value.Current))
                    {
                        return;
                    }
                    callbackStarted.Set();
                    // Block well past the pulse budget - simulates a callback that cannot be
                    // cancelled and keeps running (and touching the resource) after the manager
                    // has already given up waiting on it.
                    releaseCallback.Wait(TimeSpan.FromSeconds(5));
                    value.Current.Touch();
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
        // 1.22b - An ordinary DisposeAsync() racing a still-blocked heartbeat callback must not
        // dispose the resource either (F15, Rodada 3): the F12 fix's guard,
        // "when (!_lifetime.IsCancellationRequested)", correctly isolates a genuine pulse-budget
        // timeout, but excludes the case where _lifetime itself is what cancelled the same linked
        // pulseTimeout - an ordinary shutdown, not a timeout. That case fell through to the
        // generic catch, which disposed the resource immediately - the exact race F12 had already
        // fixed for the timeout path, reopened for the shutdown path. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, F15.
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
                    if (Interlocked.CompareExchange(ref firstProbe, value.Current, null) is not null
                        && !ReferenceEquals(firstProbe, value.Current))
                    {
                        return;
                    }
                    callbackStarted.Set();
                    // A pulse budget of 5s means DisposeAsync() below - fired well inside that
                    // budget - is an ordinary shutdown, not a pulse-budget timeout.
                    releaseCallback.Wait(TimeSpan.FromSeconds(5));
                    value.Current.Touch();
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
        // 1.29 - With initialCapacity == 2 (the minimum legal value), the R16 fix's own margin
        // formula collapses to currentCapacity itself, making scale-down from above initial
        // capacity mathematically unreachable regardless of position - including exactly at
        // MaxCapacity, not just off-tier (Rodada 4, R18). Fixed by capping the margin at
        // currentCapacity - 1. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, R18.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AutoScaleAcquireFault_WithMinimumLegalInitialCapacity_StillScalesDownFromMaxCapacity()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMinimumInitialCapacityScaleDown", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 3, TimeSpan.FromMilliseconds(600))
                .AutoScaleAcquireFault(1)
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 2; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - this acquire times out and enqueues the autoscale Fault, scaling
            // 2 -> 6 (MaxCapacity).
            var faulted = await service.AcquireAsync();
            Assert.False(faulted.Successful);

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the fault-triggered scale-up to reach max capacity.");

            // Release everything so the pool becomes fully idle - before the R18 fix, this could
            // never scale down from here, no matter how idle, because initialCapacity == 2 made
            // the margin mathematically unreachable even at the exact maximum capacity.
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.IsMaxCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsMaxCapacity, "Expected scale-down from MaxCapacity to still be reachable when initialCapacity is the minimum legal value (2), instead of being stuck there forever.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.23 - maxConsecutiveFactoryFailures (R14, Rodada 2): default (0) must keep today's
        // fail-fast behavior (a single item's failure still gives up on the rest of the batch);
        // opting in to a higher value must let the batch keep trying the remaining not-yet-
        // attempted items instead. Before this parameter existed, CreateItemsAsync always rethrew
        // on the very first per-item timeout/exception, with no way to opt into anything else, even
        // though the overall deadline (quantity * FactoryTimeout) had plenty of room left to try the
        // rest. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, R14.
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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // Default tolerance (0) preserves the original behavior: only the 1 item attempted
            // before the failure (call 3) is kept - calls 5 and 6 are never even attempted.
            Assert.False(moved);
            Assert.Equal(3, service.CurrentCapacity);

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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // The batch did not fully complete (call 4 never made it in), but 3 of the 4 requested
            // items (calls 3, 5, 6) must still have been created - not just the 1 attempted before
            // the mid-batch failure.
            Assert.False(moved);
            Assert.Equal(5, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.24 - A normal DisposeAsync racing an in-progress scale-up or heartbeat-triggered
        // replacement must not be logged as a factory TimeoutException (R15, Rodada 2): before
        // this fix, the outer OperationCanceledException catches in CreateItemsAsync and
        // CreateSingleReplacementAsync did not distinguish "the factory/overall deadline actually
        // elapsed" from "DisposeAsync cancelled the lifetime token while this was in flight" -
        // both were logged identically as a timeout, misleading an on-call engineer into thinking
        // the factory/broker was unhealthy during an ordinary clean shutdown. See TODO/relatorio-
        // viabilidade-ringbufferplus-v5.md, R15.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnInProgressScaleUp_DoesNotLogAFalseFactoryTimeout()
        {
            var errors = new List<Exception>();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringScaleUpNoFalseTimeout", null);
            var service = builder
                .Factory(async ct => { await Task.Delay(TimeSpan.FromSeconds(2), ct); return 1; }, TimeSpan.FromSeconds(5))
                .OnError((_, ex) => errors.Add(ex))
                .ElasticCapacity(2, 2, 5, 1, TimeSpan.FromSeconds(30))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
                .OnError((_, ex) => errors.Add(ex))
                .HeartBeat(value =>
                {
                    value.Invalidate();
                    invalidated.Set();
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
        // as "RingBuffer did not reach initial capacity" (Finding A, Rodada 3 - Resiliência): the
        // warmup completion wait is bounded by _lifetime.Token so a concurrent dispose does not
        // hang it forever, but the catch that observes that cancellation treated it identically to
        // a genuine factory failure to reach capacity, logging it as an ERROR during an ordinary
        // clean shutdown. Same bug class as R15, in a code path R15 did not touch. See TODO/
        // relatorio-viabilidade-ringbufferplus-v5.md, Finding A (Resiliência, Rodada 3).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_RacingAnInProgressWarmup_DoesNotLogAFalseCapacityFailure()
        {
            var errors = new List<Exception>();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeDuringWarmupNoFalseFailure", null);
            var service = builder
                .Factory(async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return 1; }, TimeSpan.FromSeconds(10))
                .OnError((_, ex) => errors.Add(ex))
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
        // 1.26 - A fault-triggered scale-up that only partially succeeds (R14's tolerated
        // failures) can land off-tier, strictly between Capacity and MaxCapacity. Idleness there
        // must still eventually trigger a scale-down (R16, Rodada 3) instead of getting stuck at
        // that off-tier capacity forever: AutoScaleDecision.EvaluateScaleDown originally only ever
        // evaluated at the exact initial or maximum capacity, using a safety margin computed once
        // from Min/Init/MaxCapacity - unreachable from most off-tier positions. See TODO/
        // relatorio-viabilidade-ringbufferplus-v5.md, R16.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AutoScaleAcquireFault_PartialScaleUpLandsOffTier_StillEventuallyScalesDown()
        {
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractOffTierScaleDown", null);
            var service = builder
                .Factory(_ =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    if (n <= 4)
                    {
                        // Warmup fill (Capacity=4) - always succeeds.
                        return Task.FromResult(1);
                    }
                    var scaleUpAttempt = n - 4;
                    if (scaleUpAttempt % 2 == 1)
                    {
                        // Odd attempts fail, even attempts succeed - never 2 consecutive
                        // failures, so maxConsecutiveFactoryFailures: 1 tolerates every one of
                        // them and the batch runs to completion. 3 of the 6 requested items
                        // succeed, landing capacity at 4 + 3 = 7, strictly between Capacity(4)
                        // and MaxCapacity(10).
                        throw new InvalidOperationException("Simulated transient factory failure.");
                    }
                    return Task.FromResult(1);
                }, TimeSpan.FromSeconds(5), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(4, 2, 10, 3, TimeSpan.FromMilliseconds(600))
                .AutoScaleAcquireFault(1)
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - this acquire times out and enqueues the autoscale Fault, which
            // attempts to scale 4 -> 10 but only partially succeeds (3 of 6), landing at 7.
            var faulted = await service.AcquireAsync();
            Assert.False(faulted.Successful);

            var landedOffTierDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity != 7 && DateTime.UtcNow < landedOffTierDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(7, service.CurrentCapacity);

            // Release everything so the pool becomes fully idle - the scale-down-eligible
            // condition.
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity == 7 && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.CurrentCapacity < 7, "Expected the off-tier capacity (7) to still be eligible for scale-down once idle, instead of being stuck there forever.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.27 - A heartbeat tick's own internal acquire can race DisposeAsync() in a narrow window
        // where _disposed is already true but _lifetime.Token has not yet observed cancellation
        // (Rodada 4, Estabilidade): AcquireCoreAsync's ObjectDisposedException.ThrowIf(_disposed,
        // this) throws in that window, and RunHeartbeatAsync's outer catch only caught
        // OperationCanceledException - the ObjectDisposedException propagated out, faulted
        // _heartbeatTask, and DisposeAsync's own Task.WhenAll(pending) generic catch logged it as
        // an unexpected error, indistinguishable from a genuine fault. Same bug class as
        // R15/F15/R17 (an ordinary shutdown miscategorized as a failure), in a location none of
        // those fixes touched. This is a narrow, timing-dependent race, not deterministically
        // reproducible on demand - reproduced probabilistically over many iterations with a very
        // small PulseHeartBeat to maximize the hit rate, per the red/green protocol's guidance for
        // races too narrow to hit reliably a single time.
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
                    .OnError((_, ex) => errors.Add(ex))
                    .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(1))
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
        // verifying the fix above (Rodada 4, Estabilidade): if a fast heartbeat callback finishes
        // at nearly the same instant an ordinary DisposeAsync() cancels _lifetime, the F12/F15
        // catch's own guard ("!heartbeatWork.IsCompleted") can evaluate false even though this was
        // just an ordinary shutdown - the exception then fell through to the generic catch, which
        // logged the resulting OperationCanceledException/TaskCanceledException as an
        // unconditional error. Disposing the resource in that fallthrough was still safe (the
        // callback had genuinely already finished) - only the log level was wrong.
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
                    .OnError((_, ex) => errors.Add(ex))
                    .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(1))
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
        // heartbeat callback's deferred dispose (F12/F15) to finish, not merely schedule it and
        // return (Rodada 4, Estabilidade): the deferred continuation was fire-and-forget, so
        // DisposeAsync's own Task.WhenAll(pending) never included it - the pooled resource could
        // still be undisposed by the time DisposeAsync() returned, a real leak if the host process
        // exits shortly after. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, Rodada 4.
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
                    if (Interlocked.CompareExchange(ref firstProbe, value.Current, null) is not null
                        && !ReferenceEquals(firstProbe, value.Current))
                    {
                        return;
                    }
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                }, pulse: TimeSpan.FromMilliseconds(500))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 500ms pulse timeout actually fire while the callback is still blocked, so
            // the F12/F15 deferred-dispose continuation gets enqueued.
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
        // 1.29 - DisposeAsync()'s idle-item drain loop (Rodada 5, Estabilidade, Finding A) was not
        // actually unconditional, despite its own comment claiming so: a raw loop calling
        // DisposeItemAsync directly for each idle item aborted on the first one whose Dispose()
        // threw, leaking every remaining item plus _lifetime/_meter/_activitySource - permanently,
        // since _disposeGuard makes a second DisposeAsync() call a silent no-op. Same failure mode
        // R2 already fixed once, for the warmup-exception trigger; this is the same defect via a
        // different trigger (an item's own Dispose() failing instead). Fixed by routing through
        // the existing DisposeItemsDefensivelyAsync helper (already used by RemoveItemsAsync),
        // which disposes every item regardless of any individual failure and logs instead of
        // propagating. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, Rodada 5.
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
                .OnError((_, ex) => errors.Add(ex))
                .FixedCapacity(3)
                .BuildWarmupAsync();

            await service.DisposeAsync();

            Assert.Equal(3, probes.Count);
            Assert.All(probes, p => Assert.True(p.Disposed, "Expected every idle item to be disposed, even though one of them threw."));
            Assert.Contains(errors, ex => ex is InvalidOperationException);
        }

        // ---------------------------------------------------------------------
        // 1.30 - With BackgroundLogger(true), DisposeAsync() completed and drained _logQueue (and
        // awaited _loggerTask) before its own later log calls - the WhenAll-failure catch, the
        // deferred-heartbeat-disposal grace-period message, and the item-drain loop's own defensive
        // logging - ever ran, silently dropping every one of them (Rodada 5, Estabilidade, Finding
        // B): LogMessage/LogError only ever TryWrite to that queue in background mode, with no
        // synchronous fallback. Fixed by deferring _logQueue's completion and _loggerTask's await
        // to the very end of DisposeAsync, after every possible log call above has already run.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WithBackgroundLoggerEnabled_StillDeliversItsOwnLateLogMessages()
        {
            var logger = new CapturingLogger();
            using var callbackStarted = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractDisposeBackgroundLoggerLateMessages", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(logger)
                .BackgroundLogger(true)
                .HeartBeat(_ =>
                {
                    callbackStarted.Set();
                    // Never actually released within the test - stays blocked well past
                    // DisposeAsync's own PulseHeartBeat-bounded grace period for the deferred
                    // dispose, forcing the grace-period-timeout LogMessage this test is about.
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                }, pulse: TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 300ms pulse timeout fire while the callback is still blocked, enqueueing the
            // F12/F15 deferred-dispose continuation.
            await Task.Delay(500);

            await service.DisposeAsync();

            Assert.Contains(logger.Messages, m => m.Contains("did not wait for"));
        }

        // ---------------------------------------------------------------------
        // 1.31 - _pendingHeartbeatDisposals (F12/F15's deferred-dispose bag) was only ever
        // pruned by DisposeAsync itself, at the very end of the buffer's life - a HeartBeat
        // callback that chronically overran its own pulse budget added one entry per timed-out
        // pulse for as long as the buffer stayed alive, even though almost every one of those
        // entries had already completed by the time the next pulse timed out. Unbounded growth
        // for the buffer's entire runtime, not a correctness bug (Round 5, Estabilidade). Fixed
        // by pruning already-completed entries out of the bag every time a new one is added.
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

            var bag = (System.Collections.Concurrent.ConcurrentBag<Task>)GetPrivateField(service, "_pendingHeartbeatDisposals");
            Assert.True(bag.Count <= 2, $"Expected the deferred-disposal bag to stay bounded despite {callbackCount} timed-out pulses, but it grew to {bag.Count} entries.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.32 - F23 (Rodada 6, achado independentemente por Estabilidade e Observabilidade): no
        // call to a user-supplied Logger/ErrorHandler was guarded against that callback itself
        // throwing. On the heartbeat-timeout path, that meant a throwing OnError, invoked from
        // LogError right before the ReplaceOne command is enqueued, aborted the whole catch block
        // before ReplaceOne ever ran - permanently losing one pool slot (the stuck item is never
        // replaced) and faulting _heartbeatTask. DisposeAsync()'s own Task.WhenAll(pending) then
        // observes that fault, calls LogError(ex) to report it, which invokes the same throwing
        // OnError again - this time with nothing catching it, making DisposeAsync() itself throw,
        // directly contradicting its own "must never throw" design comment.
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
                .OnError((_, _) => throw new InvalidOperationException("user OnError sink bug"))
                .HeartBeat(_ =>
                {
                    callbackStarted.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(10));
                }, pulse: TimeSpan.FromMilliseconds(200))
                .AcquireTimeout(TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2)), "Expected the heartbeat callback to start.");
            // Let the 200ms pulse timeout fire while the callback is still blocked - this is what
            // triggers the throwing OnError call inside RunHeartbeatAsync's F12/F15 catch.
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
        // 1.33 - F23's BackgroundLogger-specific half: with BackgroundLogger(true), a throwing
        // OnError is invoked from inside RunLoggerAsync's own dispatch loop - unguarded, this
        // faulted the pump on the very first bad message and silently dropped every message for
        // the rest of the instance's life, with no exception surfacing anywhere (the queue is
        // unbounded and nothing was left reading it).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task BackgroundLogger_WhenOnErrorThrowsOnce_StillDeliversLaterMessages()
        {
            var callCount = 0;
            var delivered = new List<Exception>();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBackgroundLoggerOnErrorThrowsOnce", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(new CapturingLogger())
                .BackgroundLogger(true)
                .OnError((_, ex) =>
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
            Assert.True(callCount >= 3, $"Expected at least 3 OnError invocations (the pump must survive the first throw), got {callCount}.");
            Assert.NotEmpty(delivered);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.34 - Sweep for unguarded external-callback invocations (Round 7): TurnbackAsync's
        // Invalidate() branch called DisposeItemAsync(value.Current) then
        // _commands.Writer.TryWrite(EngineCommand.ReplaceOne()) with no guard around the dispose
        // call - a user item type throwing from Dispose()/DisposeAsync() there skipped the
        // ReplaceOne enqueue entirely, permanently losing that pool slot. Same shape of bug as
        // F19/F23: a later necessary step skipped because an earlier one, calling into
        // external/user code, threw uncaught. The exception itself is expected to still
        // propagate to the caller unchanged (no public contract change, unlike F19/F23 - this
        // one is fixed with a `finally`, not a swallow) - only the replacement bookkeeping must
        // not depend on that call succeeding.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Invalidate_WhenItemsDisposeThrows_StillQueuesAReplacement()
        {
            var callIndex = 0;
            IRingBufferBuilder<ThrowingOnDisposeProbe> builder = new RingBufferBuilder<ThrowingOnDisposeProbe>("ContractInvalidateThrowingDispose", null);
            var service = await builder
                .Factory(_ =>
                {
                    var n = Interlocked.Increment(ref callIndex);
                    return Task.FromResult(new ThrowingOnDisposeProbe(throwOnDispose: n == 1));
                })
                .AcquireTimeout(TimeSpan.FromMilliseconds(300))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
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
        // 1.35 - Same unguarded-callback sweep (Round 7), same class of bug as F23, but in
        // RingBufferBuilder<T> instead of RingBufferManager<T>: ValidateBuild's own LogError(err)
        // calls invoked a throwing OnError with no guard, so the ErrorHandler's own bug replaced
        // the real validation failure Build() was about to throw. Lower severity than F23/F24 -
        // no running instance/pool state exists yet at this point - but same fix pattern.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public void Build_WhenOnErrorThrows_StillSurfacesTheRealValidationFailure()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBuildThrowingOnError", null);
            var fixedBuilder = builder
                .Logger(new CapturingLogger())
                .OnError((_, _) => throw new InvalidOperationException("user OnError sink bug"))
                .FixedCapacity(2);

            var ex = Assert.Throws<InvalidOperationException>(() => fixedBuilder.Build());
            Assert.Equal("The command Factory is not defined.", ex.Message);
        }

        // ---------------------------------------------------------------------
        // 1.39-1.40 - N1/N2 (Round 7, Estabilidade): a pooled item's own Dispose()/DisposeAsync()
        // had no bound anywhere - unlike Factory (FactoryTimeout) and the heartbeat callback
        // (PulseHeartBeat, F16). N1: a hang in DisposeAsync()'s own idle-item drain loop just
        // delayed/blocked shutdown itself. N2, far worse: RemoveItemsAsync runs on the single-
        // consumer engine's own thread during a scale-down, so a hang there stalled every other
        // command forever, including the wait DisposeAsync() itself has on _engineTask. Both fixed
        // by bounding each item's dispose wait to PulseHeartBeat (same grace-period precedent F16
        // already established) inside the shared DisposeItemsDefensivelyAsync helper.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenAnIdleItemsDisposeHangsForever_StillReturnsWithinTheGracePeriod()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractDrainLoopHangingDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingDisposeProbe(releaseHang)))
                .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(200))
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

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleDown_WhenAnIdleItemsDisposeHangsForever_StillLetsTheEngineProcessLaterCommands()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingDisposeProbe> builder = new RingBufferBuilder<HangingDisposeProbe>("ContractScaleDownHangingDispose", null);
            var service = builder
                .Factory(_ => Task.FromResult(new HangingDisposeProbe(releaseHang)))
                .ElasticCapacity(4, 2, 4, 1, TimeSpan.FromSeconds(5))
                .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(200))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Scale down 4 -> 2: RemoveItemsAsync pulls 2 idle items whose Dispose() hangs forever.
            var switchTask = service.SwitchToAsync(ScaleSwitch.MinCapacity);
            var completed = await Task.WhenAny(switchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(switchTask, completed);

            // The engine must still be alive for further work - a second, unrelated command must
            // not be stuck behind the first scale-down's hung item disposals.
            var secondSwitchTask = service.SwitchToAsync(ScaleSwitch.InitCapacity);
            var secondCompleted = await Task.WhenAny(secondSwitchTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(secondSwitchTask, secondCompleted);

            releaseHang.Set();
            var disposeTask = service.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(disposeTask, disposeCompleted);
        }
    }
}
