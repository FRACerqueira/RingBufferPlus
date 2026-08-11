// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Behavioral contract tests for the v4 -> v5 concurrency rewrite (see doc/action-plan.md, Phase 1/2,
// and doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md).
//
// This suite started (Phase 1) as a set of tests describing the INTENDED v5 behavior, with every
// regression case marked [Fact(Skip = "...")] because it failed against the v4 implementation.
// Phase 2 replaced RingBufferManager<T> with the Channel-based, single-consumer engine these
// contracts describe, so every test below is now ported to the v5 API and unskipped - this file
// IS the Phase 2 acceptance gate (action-plan.md, Phase 2 "Acceptance criterion"), and it is green.
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
    }
}
