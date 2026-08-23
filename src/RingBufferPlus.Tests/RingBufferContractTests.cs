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
                .ElasticCapacity(3, 2, 6, 1, TimeSpan.FromSeconds(5))
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
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)))));

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
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)));
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
        // Round 8, Resiliência Finding 3: on the unlocked SwitchToAsync path (LockWhenScaling not
        // set), the caller never awaits completion.Task - `!LockWhenScaling || await
        // completion.Task...` short-circuits before the right side is ever evaluated. If the engine
        // loop later resolves that TCS via TrySetException on a genuine scale failure, nothing ever
        // observes the fault, and it surfaces as a genuinely unobserved task exception once the TCS
        // is garbage-collected.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenUnlockedAndTheScaleUpFails_DoesNotSurfaceAnUnobservedTaskException()
        {
            var scaleUpShouldThrow = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractUnlockedSwitchUnobservedException", null);
            var service = await builder
                .Factory(_ => scaleUpShouldThrow ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .OnError((_, _) => { })
                .ElasticCapacity(2, 2, 4, 1, TimeSpan.FromSeconds(5))
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
        public async Task ElasticAutoscale_WhenTriggeredScaleUpFactoryThrows_EngineSurvives_AndAutoscaleRecovers()
        {
            // Arrange: init capacity 4, min 2 (init != min, to avoid the separate, already-known R4
            // defect where the scale-up target formula picks a no-op when init == min); the
            // backlog-reactive signal is unconditionally active for any elastic pool since
            // ADR001V03/ADR007V03 - no toggle needed (it replaces the old fault-count trigger this
            // test originally targeted); factory throws only while "throwing" is true.
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFaultTriggeredScaleThrows", null);
            var service = builder
                .Factory(_ => throwing ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(4, 2, 6, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            // Exhaust the pool so the next caller starts waiting and triggers the backlog-reactive
            // signal immediately.
            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            var held3 = await service.AcquireAsync();
            var held4 = await service.AcquireAsync();

            throwing = true;
            var faultedTask = service.AcquireAsync().AsTask();
            var faultedCompleted = await Task.WhenAny(faultedTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(faultedTask, faultedCompleted);
            Assert.False((await faultedTask).Successful);

            // Give the engine a moment to process the backlog-triggered scale-up (posted fire-and-forget).
            await Task.Delay(300);

            // Assert: capacity did not move (the scale-up's factory call failed), but the engine is
            // still alive - before the original fix (now applying to the backlog path instead of
            // the retired fault-count path), this permanently killed the engine and disabled
            // autoscale.
            Assert.True(service.IsInitCapacity);

            // Recover the factory: autoscale must still work. Backlog-reactive requests exactly
            // the net gap (ADR001V03: proportional, not a coarse jump to MaxCapacity), so with one
            // caller waiting at a time this grows capacity by 1 per successful batch - keep
            // triggering waits (each either times out, or succeeds and immediately consumes the
            // just-created item, keeping the pool empty for the next one) until MaxCapacity.
            throwing = false;
            var growthProbes = new List<RingBufferValue<int>>();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                var probe = await service.AcquireAsync();
                if (probe.Successful)
                {
                    growthProbes.Add(probe);
                }
            }
            Assert.True(service.IsMaxCapacity);

            foreach (var probe in growthProbes)
            {
                await probe.DisposeAsync();
            }
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
            private volatile bool _throwOnDispose;
            public ThrowingOnDisposeProbe(bool throwOnDispose) => _throwOnDispose = throwOnDispose;
            // Settable post-construction so a test can pick, by identity, which specific acquired
            // instance throws - v6.0.0's bounded-concurrent Fábrica (ADR001V03) creates a batch's
            // items concurrently, so "the Nth factory call" no longer reliably corresponds to "the
            // Nth item that ends up acquired"; tests that need one specific *acquired* item to
            // throw must flip this after acquiring, not bake it into the factory by call order.
            public bool ThrowOnDispose { set => _throwOnDispose = value; }
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

        // Round 8 (Observabilidade): a plain synchronous IDisposable, unlike HangingDisposeProbe
        // above - its Dispose() blocks the calling thread directly, with no await point of its own.
        private sealed class HangingSyncDisposeProbe(ManualResetEventSlim release) : IDisposable
        {
            // Capped at 10s so a broken fix can't actually hang the test process forever - the
            // assertions themselves are what prove the library-level bound (PulseHeartBeat) works.
            public void Dispose() => release.Wait(TimeSpan.FromSeconds(10));
        }

        // Round 8 (Resiliência/F30): hangs past the grace period, then - once released - faults on
        // its way out. Used to land a background dispose fault (the ContinueWith in
        // DisposeOneItemDefensivelyAsync's TimeoutException branch) AFTER _logQueue has already
        // been completed by DisposeAsync's own finally block.
        private sealed class HangingThenThrowingDisposeProbe(ManualResetEventSlim release) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await Task.Run(() => release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                throw new InvalidOperationException("Simulated late dispose failure after grace period.");
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
        // v6.0.0 / ADR001V03 pre-work (Round 8 blast-radius sweep, paused finding): unlike its two
        // siblings (DisposeAsync()'s drain loop, RemoveItemsAsync via DisposeItemsDefensivelyAsync),
        // TurnbackAsync's Invalidate() branch awaited the old item's Dispose()/DisposeAsync() BEFORE
        // enqueuing EngineCommand.ReplaceOne() in a `finally`. A Dispose() that hangs forever means
        // that `finally` never runs (an unfinished await never lets it), so the slot is never
        // replaced and CurrentCapacity is wrong forever from that point - same bug shape as
        // F27/F28/F29, a third call site those fixes did not touch.
        //
        // Fixed by enqueuing the replacement first, unconditionally, before awaiting the old item's
        // disposal: pool-wide capacity truthfulness must not depend on how long, or whether, that
        // call ever returns - a caller-owned item type whose Dispose() hangs is that caller's own
        // problem (their own DisposeAsync() call on the RingBufferValue<T> hangs too), not a reason
        // for shared pool state to go wrong for everyone else. This is the "prefer truthful state
        // over another track-and-observe guard" lens adopted in Round 8: the blast radius here is
        // local (one caller's own call blocks), not global (nothing about the engine loop or a
        // background pump depends on this call returning), so the fix is a direct reordering, not a
        // fourth instance of the PulseHeartBeat-bounded defensive-dispose pattern.
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
        // Companion to the test above: the reordered TryWrite(ReplaceOne()) now runs
        // unconditionally as the first thing in the Invalidate() branch, including after the
        // manager is already disposed - previously that combination never reached this branch's
        // channel write at all, so it was untested. TryWrite on an already-completed channel
        // (Channel<EngineCommand>.Writer.TryComplete(), no exception) returns false rather than
        // throwing, so this must fall through and dispose the item exactly once, the same as the
        // pre-existing non-Invalidate case (TurnbackAsync_AfterManagerDisposed_...) above.
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
                .ElasticCapacity(2, 2, 5, 1, TimeSpan.FromSeconds(5))
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
        // 1.15 - Autoscale-on-fault must never be permanently disabled by a legal configuration.
        // Before this fix, initialCapacity == minCapacity made the scale-up target formula pick a
        // no-op (target == current) on every single fault, regardless of how many times it fired or
        // how healthy the factory was. See TODO/relatorio-viabilidade-ringbufferplus-v5.md, finding R4.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ElasticAutoscale_WhenInitialCapacityEqualsMinCapacity_StillScalesUp()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractAutoScaleInitEqualsMin", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();

            // Pool is empty - this caller starts waiting and triggers the backlog-reactive signal
            // immediately (ADR001V03; replaces the old fault-count trigger the R4 bug originally
            // guarded against). Whether this specific acquire ends up succeeding or timing out is
            // not the point - the new target formula (CurrentCapacity + gap, capped at
            // MaxCapacity) has no comparison against Capacity/MinCapacity at all, so the R4 bug
            // class cannot recur here, but the broader "init == min must not block scale-up"
            // property is still worth keeping.
            _ = await service.AcquireAsync();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (service.IsInitCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsInitCapacity, "Expected the waiting caller to trigger a scale-up away from the initial (== minimum) capacity.");

            await held1.DisposeAsync();
            await held2.DisposeAsync();
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
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 3; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty (all 3 items in `held`) - 7 concurrent waiters trigger the backlog-
            // reactive signal (ADR001V03), growing capacity 3 -> 10, possibly via more than one
            // successive batch (it reacts proportionally to the net gap, not a coarse jump to
            // MaxCapacity like the old fault-count trigger this test originally used). The Monitor's
            // own scale-down path (ADR003V03) is unconditionally active for this elastic pool too
            // (ADR001V03/ADR007V03 - no toggle to enable it). `held` is deliberately
            // NOT released yet - releasing it before the waiters are served would let some of them
            // grab those items directly, shrinking the net gap EvaluateBacklogReactive computes and
            // making capacity land short of MaxCapacity.
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
        // 1.20 - A scale-down must not block the engine's single-consumer loop waiting for busy
        // items to be returned (R6, P3): it must only take whatever is already idle right now
        // (opportunistic, partial-if-needed), the same "keep partial progress" spirit already
        // applied to scale-up (R5/P1#8), instead of blocking every other command (Fault, another
        // Switch, ReplaceOne) behind a wait bounded by SamplesBase. See
        // TODO/relatorio-viabilidade-ringbufferplus-v5.md, R6.
        //
        // Remoção (ADR001V03) update: whether the second Switch below is accepted or rejected is
        // now a genuine race, not asserted either way - a caller-visible consequence documented on
        // the Switch case itself. Before Remoção, a scale-down's own removal was a single
        // synchronous step (dequeue only, R6's own "never wait for busy items" already made it
        // near-instant for int items specifically), so _scaling reliably cleared before this second
        // command was even posted. After Remoção, EVERY scale-down (regardless of item type) goes
        // through the same dispatch-then-confirm-on-completion cycle Fábrica's scale-up already
        // used - for int items the background batch is still near-instant, but whether it wins the
        // race against this second command's own processing is exact scheduling, observed to go
        // either way across repeated full-suite runs. What stays deterministic, and is what this
        // test actually asserts, is that the engine processes the second command promptly either
        // way, instead of being stuck behind the first scale-down's wait for busy items.
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
            var firstSwitchAccepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            Assert.True(firstSwitchAccepted);

            // A second, independent command posted right after must not be STUCK behind the
            // first one's engine-side processing - the whole point of this test. Whether it is
            // accepted or rejected (see the class remarks above) is a genuine race, not asserted.
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
        public async Task ElasticAutoscale_WithMinimumLegalInitialCapacity_StillScalesDownFromMaxCapacity()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMinimumInitialCapacityScaleDown", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 3, TimeSpan.FromMilliseconds(600))
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 2; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - 4 concurrent waiters trigger the backlog-reactive signal
            // (ADR001V03), growing capacity 2 -> 6 (MaxCapacity), possibly via more than one
            // successive batch. The Monitor's own scale-down path (what R18 is actually about,
            // originally against the now-retired median algorithm) is unconditionally active for
            // this elastic pool - no toggle to enable it (ADR001V03/ADR007V03).
            var waiterTasks = Enumerable.Range(0, 4).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the fault-triggered scale-up to reach max capacity.");

            // Every waiter must settle (served or timed out), and any served one disposed, before
            // releasing `held` below - otherwise a leaked never-disposed item would keep the pool
            // from ever becoming genuinely fully idle.
            var waiterResults = await Task.WhenAll(waiterTasks);
            foreach (var result in waiterResults)
            {
                if (result.Successful)
                {
                    await result.DisposeAsync();
                }
            }

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
        //
        // v6.0.0 (ADR001V03) made Fábrica bounded-concurrent (MaxConcurrentFactoryCalls, default 4):
        // "give up on the remaining not-yet-attempted items" now only bites items that are still
        // queued behind the concurrency window - anything already launched within it keeps running
        // regardless. maxConcurrentFactoryCalls: 1 below pins this test back to the original
        // one-at-a-time shape so it isolates the tolerance behavior from the concurrency behavior;
        // the sibling test right after this one covers the bounded-concurrency case explicitly.
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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
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
        // v6.0.0 / ADR001V03: the bounded-concurrency counterpart to the test above. With
        // maxConcurrentFactoryCalls: 2 and 8 items requested, give-up only ever stops a later wave
        // (still queued behind the concurrency window) from starting - it cannot un-start an
        // attempt already in flight. This is deliberately a bound, not an exact count: give-up
        // itself (a shared flag, set by whichever concurrent attempt fails) can race a sibling
        // attempt's own success and release of its concurrency-window slot, so at most one or two
        // stragglers beyond the wave that was already running may also start before the flag is
        // visible to them - the same "simple, not a circuit-breaker" looseness ADR001V03 accepts
        // for this mechanism under real concurrency. What must hold regardless of that race is the
        // actual guarantee: nowhere near the full batch of 8 is ever attempted.
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
                .ElasticCapacity(2, 2, 10, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 2)
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
        // v6.0.0 / ADR001V03: the core Fábrica acceptance criterion - a batch large enough to need
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
                .ElasticCapacity(2, 2, 10, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 3)
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
        // v6.0.0 / ADR001V03: Fábrica's batch now runs on the thread pool instead of blocking the
        // engine's single consumer thread (Orquestrador) inline - a second command must be
        // dequeued and handled immediately, not queued up behind the whole in-flight batch. A
        // second overlapping scale-up request is still correctly rejected (one batch at a time,
        // to avoid a MaxCapacity overshoot - see the Switch case's own comment), but the rejection
        // itself must be prompt, proving the engine loop was free to look at it right away.
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
                .ElasticCapacity(2, 2, 4, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
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
            // not even be looked at until the ~2s factory delay elapsed. With Fábrica decoupled,
            // the engine is free to dequeue and reject it immediately (_scaling is true).
            var sw = Stopwatch.StartNew();
            var secondAccepted = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            sw.Stop();

            Assert.False(secondAccepted);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
                $"Expected the engine to reject the second request promptly instead of blocking behind the in-flight batch; took {sw.Elapsed}.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // v6.0.0 / ADR001V03: Fábrica's "simple growing backoff after consecutive [genuine]
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
                .ElasticCapacity(8, 2, 16)
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
        // v6.0.0 / ADR001V03 (advisor review): the backoff wait must never be counted against a
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
                .ElasticCapacity(2, 2, 3, 1, TimeSpan.FromSeconds(5), maxConcurrentFactoryCalls: 1)
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

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));

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
        // Monitor (ADR001V03/ADR003V03): the deadband call-site wiring (not part of
        // AutoScaleMonitor.EvaluateTarget itself, which AutoScaleMonitorTests.cs already covers in
        // isolation) is what stops the Monitor from re-evaluating its own noise into perpetual
        // motion. Under demand that never changes, capacity should settle once and then stop -
        // the same oscillation-freedom property the validated simulation measured (48 reversals in
        // 300 ticks without a deadband, 1 with it).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Monitor_UnderSteadyDemand_ConvergesAndStopsChanging_NoOscillation()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMonitorNoOscillation", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(8, 2, 20, 4, TimeSpan.FromMilliseconds(800))
                .BuildWarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Let the Monitor converge from the initial steady demand (4 in use out of 8) first,
            // then sample capacity repeatedly over a further steady window - demand never changes
            // throughout, so once converged it must not keep drifting tick after tick.
            await Task.Delay(2000);
            var samples = new List<int>();
            for (var i = 0; i < 10; i++)
            {
                samples.Add(service.CurrentCapacity);
                await Task.Delay(200);
            }

            // Direction-reversal count, not just "few distinct values" (which a genuine 5,8,5,8,...
            // thrash would still pass) - mirrors the validated simulation's own CountOscillations.
            var reversals = 0;
            var lastDirection = 0;
            for (var i = 1; i < samples.Count; i++)
            {
                var delta = samples[i] - samples[i - 1];
                if (delta == 0)
                {
                    continue;
                }
                var direction = delta > 0 ? 1 : -1;
                if (lastDirection != 0 && direction != lastDirection)
                {
                    reversals++;
                }
                lastDirection = direction;
            }
            Assert.True(reversals == 0, $"Expected capacity to have settled under steady demand, not keep reversing direction. Observed sequence: {string.Join(",", samples)}.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Monitor (ADR001V03/ADR003V03): same reachability-cap bug class as the old median
        // algorithm's R18/R19 (Rodada 4), reproduced in a new shape. MonitorDeadband defaults to 3;
        // a buffer whose Capacity-to-MinCapacity span is smaller than that (here, 4 to 2 - span 2)
        // would otherwise never be able to scale down to its own floor via the Monitor, however
        // idle it becomes, because |target(2) - CurrentCapacity(4)| = 2 < deadband(3) blocks it
        // forever. The fix caps the deadband to the maximum delta actually reachable in the
        // direction being asked for, mirroring AutoScaleDecision's own Math.Min(threshold,
        // currentCapacity - 1) cap for exactly the same reason.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Monitor_WhenSpanIsSmallerThanTheDeadband_StillReachesMinCapacityWhenIdle()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMonitorDeadbandReachability", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(4, 2, 8, 4, TimeSpan.FromMilliseconds(800))
                .BuildWarmupAsync();

            Assert.Equal(4, service.CurrentCapacity);

            // Fully idle the whole time - no held items, no waiters. If MonitorDeadband(3) is not
            // capped to this buffer's own scale-down span (4 - 2 = 2), CurrentCapacity is
            // mathematically stuck at 4 forever regardless of how long this waits.
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (service.CurrentCapacity > 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.Equal(2, service.CurrentCapacity);

            await service.DisposeAsync();
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
        public async Task ElasticAutoscale_PartialScaleUpLandsOffTier_StillEventuallyScalesDown()
        {
            // Backlog-reactive (ADR001V03) is self-driving: an unserved waiting caller keeps
            // re-triggering EvaluateBacklogReactive (via the FactoryBatchCompleted follow-up call)
            // until it is either served or times out - it does not "give up partway" the way a
            // single fixed-quantity SwitchToAsync/fault-triggered batch used to. A batch composed
            // to fail on exactly half its attempts (the old design) therefore does not reliably
            // land at a fixed off-tier capacity anymore. Nor is the landing value fully
            // deterministic even with a bounded number of waiters: _waitingCount is decremented in
            // a served caller's own continuation (AcquireCoreAsync's finally block), which can run
            // slightly after a follow-up evaluation already re-reads it as "still waiting",
            // occasionally dispatching one extra small batch before the count catches up (see
            // EvaluateBacklogReactive's own remarks - a known, accepted approximation, never risks
            // exceeding MaxCapacity). MaxCapacity is set generously far from Capacity here so this
            // still reliably lands off-tier (strictly below MaxCapacity) rather than asserting an
            // exact value.
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractOffTierScaleDown", null);
            var service = builder
                .Factory(_ =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    if (n == 5)
                    {
                        // The very first scale-up attempt fails genuinely once (tolerated via
                        // maxConsecutiveFactoryFailures: 1); every other call - warmup and every
                        // later scale-up attempt - succeeds.
                        throw new InvalidOperationException("Simulated transient factory failure.");
                    }
                    return Task.FromResult(1);
                }, TimeSpan.FromSeconds(5), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(4, 2, 20, 3, TimeSpan.FromMilliseconds(600))
                .AcquireTimeout(TimeSpan.FromSeconds(1))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - 2 concurrent waiters trigger the backlog-reactive signal
            // (ADR001V03). This test's own off-tier scale-down check depends on the Monitor's
            // scale-down path (ADR003V03), unconditionally active for this elastic pool - no toggle
            // to enable it (ADR001V03/ADR007V03).
            var waiterTasks = Enumerable.Range(0, 2).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            // Capacity must both move away from its initial value (a genuine failure happened and
            // backlog reacted) and settle (stop changing for a short, quiet window) before reading
            // it as "landed" - otherwise a still-in-flight extra round (see the class remarks
            // above) could be read as final when it is not.
            var landedOffTierDeadline = DateTime.UtcNow.AddSeconds(5);
            int landedCapacity;
            while (true)
            {
                landedCapacity = service.CurrentCapacity;
                await Task.Delay(150);
                if (landedCapacity > 4 && landedCapacity == service.CurrentCapacity)
                {
                    break;
                }
                Assert.True(DateTime.UtcNow < landedOffTierDeadline, $"Expected capacity to move away from initial (4) and settle within the deadline. Last observed: {service.CurrentCapacity}.");
            }
            Assert.True(landedCapacity > 4 && landedCapacity < 20, $"Expected an off-tier landing strictly between Capacity(4) and MaxCapacity(20). Actual: {landedCapacity}.");
            // Ties the off-tier landing to the injected failure itself, not just to "capacity grew
            // somehow": n == 5 throws exactly once ever (callCount only increases), so every
            // factory invocation beyond the items actually landed in the pool is that one lost
            // attempt. A factory that never failed would have callCount == landedCapacity exactly,
            // which this rules out.
            Assert.Equal(landedCapacity + 1, Volatile.Read(ref callCount));

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

            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity == landedCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.CurrentCapacity < landedCapacity, $"Expected the off-tier capacity ({landedCapacity}) to still be eligible for scale-down once idle, instead of being stuck there forever. Actual: {service.CurrentCapacity}");

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
        // Round 8 (F30): _logQueue is unbounded, so LogMessage/LogWarning/LogError's TryWrite only
        // ever fails once the queue has been completed - DisposeAsync completes it only after the
        // final item-drain loop returns, but a hung item's background dispose (Round 8 fix above)
        // can fault well after that, once released. Before this fix that late LogError call was
        // silently dropped.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task LogError_ForABackgroundDisposalThatFaultsAfterDisposeAsyncReturned_IsStillDelivered_NotSilentlyDropped()
        {
            using var releaseHang = new ManualResetEventSlim();
            var errors = new List<Exception>();

            IRingBufferBuilder<HangingThenThrowingDisposeProbe> builder = new RingBufferBuilder<HangingThenThrowingDisposeProbe>("ContractLateBackgroundDisposalFaultNotDropped", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingThenThrowingDisposeProbe(releaseHang)))
                .Logger(new CapturingLogger())
                .BackgroundLogger(true)
                .OnError((_, ex) => { lock (errors) errors.Add(ex); })
                .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // Returns once the grace period elapses for the hung idle item - _logQueue is now
            // completed (DisposeAsync's finally block already ran to the end).
            await service.DisposeAsync();

            // Only now does the background dispose actually finish, and fault - strictly after the
            // queue that LogError would normally write to has been closed.
            releaseHang.Set();

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
            // v6.0.0's bounded-concurrent Fábrica (ADR001V03) creates warmup's 2 items concurrently
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
        // Remoção (ADR001V03): the test above proves the engine survives a hung scale-down disposal
        // within DisposeAsync()'s own drain (already true before this role existed, thanks to the
        // pre-existing PulseHeartBeat grace-period bound, N1/N2). What it does NOT prove is that the
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
                .ElasticCapacity(4, 2, 4, 1, TimeSpan.FromSeconds(30))
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
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Expected the unrelated ReplaceOne's own factory call to happen promptly instead of being stuck behind the scale-down's own hung-disposal grace period (PulseHeartBeat default, 10s). Actual: {sw.Elapsed}.");

            releaseHang.Set();
            await oldItemDisposeTask;
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Round 8, Estabilidade F29 + Observabilidade: DisposeItemsDefensivelyAsync's grace period
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

        // ---------------------------------------------------------------------
        // Round 8, Estabilidade F29: the batch of idle items was disposed sequentially - N hung
        // items cost N x PulseHeartBeat in total, not one bounded wait for the whole batch.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task DisposeAsync_WhenMultipleIdleItemsAllHangOnDispose_TheWholeBatchIsBoundedByOnePulseHeartBeat_NotN()
        {
            using var releaseHang = new ManualResetEventSlim();
            IRingBufferBuilder<HangingSyncDisposeProbe> builder = new RingBufferBuilder<HangingSyncDisposeProbe>("ContractDrainLoopBatchHangingSyncDispose", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingSyncDisposeProbe(releaseHang)))
                .HeartBeat(_ => { }, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(4)
                .BuildWarmupAsync();

            var sw = Stopwatch.StartNew();
            var disposeTask = service.DisposeAsync().AsTask();
            // 4 hung items sequentially would cost ~4 x 200ms = 800ms just for the grace periods,
            // on top of real dispatch overhead - budget well below that to prove concurrency, but
            // above one grace period (200ms) plus scheduling slack.
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(700)));
            sw.Stop();

            Assert.Same(disposeTask, completed);
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(700), $"Expected the batch to be bounded by ~one PulseHeartBeat, took {sw.Elapsed}.");

            releaseHang.Set();
        }

        // ---------------------------------------------------------------------
        // 1.41 - Round 8, Resiliência: Invalidate() (and the heartbeat's stuck-item path) disposes
        // the old item BEFORE enqueuing ReplaceOne, so by the time CreateSingleReplacementAsync
        // runs, one real item is already gone from the pool. When its Factory call then fails, the
        // method only logged - _currentCapacity was never touched - so CurrentCapacity reported the
        // old, too-high number forever, with no retry and no correction. This is the same class of
        // bug as Round 1's R1 ("perda silenciosa de capacidade via Invalidate()"), which was marked
        // closed at the time but was never actually fixed for this code path.
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
                .OnError((_, ex) => { lock (errors) errors.Add(ex); })
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
            // The floor guard (ADR001V03, added after this test) does retry this in the background
            // once CurrentCapacity(1) < MinCapacity(2) - but replacementShouldThrow never flips
            // back to false in this test, so every retry keeps failing and capacity never recovers.
            // See Invalidate_WhenTheReplacementFactoryFailsThenRecovers_FloorGuardRestoresMinCapacity
            // below for the self-healing case this test does not cover.

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.41b - Floor guard (ADR001V03): the gap the test above documents (a failed replacement
        // shrinks CurrentCapacity below MinCapacity with nothing that retries it) is now closed -
        // the Orquestrador keeps retrying, at the pace of Fábrica's own existing consecutive-failure
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
                .OnError((_, ex) => { lock (errors) errors.Add(ex); })
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
        // 1.42-1.43 - Round 8, Resiliência Achado 1: CreateItemsAsync passed Factory the batch-level
        // token (overall.Token) and CreateSingleReplacementAsync passed it the full-lifetime token
        // (_lifetime.Token) - neither is the token that actually fires at the per-item FactoryTimeout
        // deadline (factoryTimeout.Token, itself linked from the other so it fires on every condition
        // the old token did, plus the per-item deadline). A cooperative factory that honors
        // CancellationToken was therefore never actually told to stop once .WaitAsync(factoryTimeout.Token)
        // gave up waiting on it - it kept running, orphaned, and any eventual successful result was
        // silently dropped without disposal. Passing factoryTimeout.Token instead costs nothing and lets
        // a well-behaved factory actually stop. A factory that ignores cancellation entirely is
        // unaffected by this fix and remains a documented caller responsibility - see the XML doc on
        // Factory's `value` parameter.
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
                .ElasticCapacity(2, 2, 4)
                .BuildWarmupAsync();

            _ = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)); // triggers CreateItemsAsync(quantity: 2)

            var completed = await Task.WhenAny(cancelledPromptly.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(cancelledPromptly.Task, completed);

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
                .ElasticCapacity(2, 2, 10, 3, TimeSpan.FromMilliseconds(300))
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
            // the pin above were not suppressing it (see the sibling _AfterPinExpires_ test, which
            // proves this same setup does scale down once the pin is gone).
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
                .ElasticCapacity(2, 2, 10, 3, TimeSpan.FromMilliseconds(300))
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
        public async Task FloorGuard_DuringAnActivePin_StillRestoresCapacity()
        {
            var shouldFail = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFloorGuardDuringPin", null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
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
                .ElasticCapacity(4, 2, 10, 1, TimeSpan.FromSeconds(5))
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

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithNonPositivePinDuration_ThrowsArgumentOutOfRange()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractPinDurationValidation", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 2, 6)
                .Build();
            await service.WarmupAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.Zero));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(-1)));

            await service.DisposeAsync();
        }
    }
}
