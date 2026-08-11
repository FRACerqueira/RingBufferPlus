// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Behavioral contract tests for the v4 -> v5 concurrency rewrite (see doc/action-plan.md, Phase 1,
// and doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md).
//
// These tests describe the system's INTENDED behavior, not the current v4 implementation.
// They are split in two groups:
//
//   - Baseline contracts (1.7, plus the synchronous half of 1.4): behavior that already
//     works today and MUST stay green. These are NOT skipped — they are the regression
//     net proving the Phase 2 rewrite did not change existing semantics. Do not skip these.
//
//   - Regression contracts (1.1, 1.2, 1.3, 1.5, 1.6, plus the async half of 1.4):
//     behavior that the current v4 RingBufferManager violates due to known
//     concurrency/API-surface bugs, or that cannot even be expressed yet because the
//     v5 member (e.g. DisposeAsync) does not exist. Each is marked [Fact(Skip = "...")]
//     with the bug/reason and the ADR that authorizes the fix, so CI stays green today.
//     Un-skipping every one of them with zero failures is the Phase 2.5 acceptance gate
//     for the rewrite (see action-plan.md, Phase 2 "Acceptance criterion"). Note: 1.3's
//     race has no natural async suspension point to hold onto deterministically, so its
//     reproduction against v4 is best-effort, not guaranteed on every single run —
//     that is left as-is rather than chased to perfect determinism at this stage.
//
// Caveat: the test project currently targets net10.0 only (single TFM). These contracts
// are not yet validated across the net8.0/net9.0/net10.0 matrix published for the library
// -- that lands with Phase 3.1 (ADR002).

using System.Collections.Concurrent;
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
                AcquireDelayAttempts = TimeSpan.FromMilliseconds(20),
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
        // 1.7 - Baseline contracts. Must pass today. Do not skip.
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

            manager.Dispose();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_ReturnsFalse_WhenAlreadyAtRequestedTarget()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("ContractNoOpSwitch", null);
            builder.Capacity(5);
            builder.LockWhenScaling();
            builder.MinCapacity(2);
            builder.MaxCapacity(10);
            builder.Factory(_ => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();
            Assert.True(service.IsInitCapacity);

            // Act: already at InitCapacity, asking to switch to InitCapacity again must be a no-op.
            var result = await service.SwitchToAsync(ScaleSwitch.InitCapacity);

            // Assert
            Assert.False(result);

            service.Dispose();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WithoutLockWhenScaling_ReturnsImmediately_AndScalesInBackground()
        {
            // Arrange: LockWhenScaling is NOT set (defaults to false) -> background, non-blocking scaling.
            var builder = new RingBufferBuilder<int>("ContractBackgroundSwitch", null);
            builder.Capacity(3);
            builder.MinCapacity(2);
            builder.MaxCapacity(6);
            builder.Factory(_ => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();

            // Act
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // Assert: the request is accepted (enqueued) immediately...
            Assert.True(accepted);

            // ...and eventually takes effect in the background.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity);

            service.Dispose();
        }

        // ---------------------------------------------------------------------
        // 1.1 - Concurrent warmup idempotency
        // ---------------------------------------------------------------------

        [Fact(Skip = "Fails on v4 - Startup() checks _WarmupDone/_WarmupRunning outside any lock, so concurrent WarmupAsync callers on a cold buffer can duplicate the factory calls beyond Capacity; fixed by the Channel-based rewrite. Un-skip as part of ADR001, Phase 2.5.")]
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

            // Deterministically widen the race window instead of hoping thread scheduling
            // hits it: hold _semaphoreBuffer ourselves first. Startup() only sets
            // _WarmupRunning AFTER acquiring this same semaphore, so while we hold it,
            // any number of WarmupAsync() callers will all pass the unprotected
            // "_WarmupDone || _WarmupRunning" check (both still false) and queue up
            // behind us -- reproducing the race every time, not just sometimes.
            var semaphore = (SemaphoreSlim)GetPrivateField(manager, "_semaphoreBuffer");
            await semaphore.WaitAsync();

            var warmupA = manager.WarmupAsync();
            var warmupB = manager.WarmupAsync();

            // Act: release the gate, letting both queued callers proceed.
            semaphore.Release();
            await Task.WhenAll(warmupA, warmupB);

            // Assert: the factory must be invoked exactly Capacity times, never duplicated.
            Assert.Equal(5, factoryCalls);

            manager.Dispose();
        }

        // ---------------------------------------------------------------------
        // 1.2 - Cancellation during a scale operation must not corrupt the semaphore
        // ---------------------------------------------------------------------

        [Fact(Skip = "Fails on v4 - ScaleUpProcessAsync's finally always calls _semaphoreBuffer.Release(), even when the matching WaitAsync was cancelled and never acquired the lock, throwing an unhandled SemaphoreFullException; fixed by the Channel-based rewrite. Un-skip as part of ADR001, Phase 2.5.")]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_CancelledWhileAcquiringSemaphore_DoesNotThrowUnhandled()
        {
            // Arrange
            var manager = CreateFixedManager(4, _ => Task.FromResult(1));
            await manager.WarmupAsync();

            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            var item = new ScaleParameters(ScaleSwitch.MaxCapacity, ScaleSwitch.InitCapacity, 2, alreadyCancelled.Token);
            var method = typeof(RingBufferManager<int>).GetMethod("ScaleUpProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // Act: directly reproduce the exact WaitAsync-cancelled-before-acquire scenario.
            var task = (Task)method.Invoke(manager, [item, true])!;
            var ex = await Record.ExceptionAsync(() => task);

            // Assert: a cancelled scale attempt must complete quietly, not leak a
            // SemaphoreFullException, and the semaphore must remain usable afterwards.
            Assert.Null(ex);

            var semaphore = (SemaphoreSlim)GetPrivateField(manager, "_semaphoreBuffer");
            Assert.Equal(1, semaphore.CurrentCount);

            manager.Dispose();
        }

        // ---------------------------------------------------------------------
        // 1.3 - No conflicting concurrent scale requests get enqueued
        // ---------------------------------------------------------------------

        [Fact(Skip = "Fails on v4 (probabilistically) - the '!_autoscaleRunning' check in SwitchToAsync/AcquireAsync is read outside the '_lock' guard (check-then-act), so concurrent callers can each enqueue a duplicate scale request. Unlike 1.1/1.2, this race has no natural async suspension point a test can hold onto, so reproduction here is best-effort (empirically ~most runs at N=20), not guaranteed every run -- that is itself evidence of the missing synchronization, not a flaw to chase away before Phase 2. Fixed by the Channel-based rewrite, which removes the race by construction. Un-skip as part of ADR001, Phase 2.5.")]
        [Trait("Category", "Contract")]
        public async Task ConcurrentSwitchToAsync_AtMostOneCallerEnqueuesTheScaleRequest()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("ContractConflictingScale", null);
            builder.Capacity(4);
            builder.MinCapacity(2);
            builder.MaxCapacity(12);
            builder.Factory(_ => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();

            // Act: many concurrent callers racing to request the same scale target.
            // Task.Run forces genuine thread-pool parallelism -- see the note in
            // ConcurrentWarmup_InvokesFactoryExactlyCapacityTimes for why calling
            // SwitchToAsync directly here would never be truly concurrent. There is no
            // deterministic hook available for this specific race (see Skip reason above),
            // so this stays a best-effort reproduction rather than a perfected one.
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity))));

            // Assert: exactly one caller should have won the race and enqueued the request.
            Assert.Equal(1, results.Count(accepted => accepted));

            service.Dispose();
        }

        // ---------------------------------------------------------------------
        // 1.4 - Dispose is idempotent and background tasks end without an unhandled fault
        //
        // This is a baseline contract (like 1.7), NOT a regression case: it already
        // passes on v4 today, so it is NOT skipped, and stays part of the regression net.
        //
        // NOTE: the full 1.4 contract also covers DisposeAsync in its final form
        // (ADR005), but IAsyncDisposable does not exist on the type yet, so that half
        // cannot be expressed as a compilable test until Phase 2 introduces it. Extend
        // this test with the async half as part of Phase 2.5.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Dispose_IsIdempotent_AndBackgroundTasksEndWithoutUnhandledFault()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("ContractDisposeSafety", null);
            builder.Capacity(4);
            builder.Factory(_ => Task.FromResult(0));
            builder.HeartBeat(_ => { });
            var service = builder.Build();
            await service.WarmupAsync();

            // Act: dispose twice.
            var firstDispose = Record.Exception(() => service.Dispose());
            var secondDispose = Record.Exception(() => service.Dispose());

            // Assert: idempotent.
            Assert.Null(firstDispose);
            Assert.Null(secondDispose);

            // Assert: none of the background tasks ended in a faulted state.
            foreach (var fieldName in new[] { "_taskbufferHeartBeat", "_taskbufferLogger", "_taskbufferautoscale", "_taskbufferNoLockautoscale" })
            {
                if (GetPrivateField(service, fieldName) is Task task)
                {
                    Assert.False(task.IsFaulted, $"{fieldName} faulted: {task.Exception}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // 1.5 - Internal scale queue is released on cleanup
        // ---------------------------------------------------------------------

        [Fact(Skip = "Fails on v4 - CleanupResources disposes _blockLogger but never _blockScale, leaking the internal BlockingCollection; fixed by the Channel-based rewrite (there is no longer a separate scale queue to leak). Un-skip as part of ADR001, Phase 2.5.")]
        [Trait("Category", "Contract")]
        public async Task Dispose_ReleasesTheInternalScaleQueue()
        {
            // Arrange
            var manager = CreateFixedManager(4, _ => Task.FromResult(1));
            await manager.WarmupAsync();

            // Act
            manager.Dispose();
            var blockScale = (BlockingCollection<ScaleParameters>)GetPrivateField(manager, "_blockScale");

            // Assert: the queue must have been released - using it after Dispose should throw.
            Assert.Throws<ObjectDisposedException>(() => blockScale.Add(new ScaleParameters(null, null, 1, CancellationToken.None)));
        }

        // ---------------------------------------------------------------------
        // 1.6 - WarmupRingBufferAsync honors its token and throws when the buffer is missing
        // ---------------------------------------------------------------------

        [Fact(Skip = "Fails on v4 - WarmupRingBufferAsync ignores the 'token' parameter it receives, always using ApplicationStopping/None instead; fixed by the HostingExtensions signature fix (Phase 2.3).")]
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

        [Fact(Skip = "Fails on v4 - the 'buffer not found' path calls ArgumentNullException.ThrowIfNull on a non-null interpolated string, which never throws, so a missing buffer silently no-ops instead of throwing; fixed by the HostingExtensions signature fix (Phase 2.3).")]
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
