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

        // ---------------------------------------------------------------------
        // 1.4c - AcquireAsync after disposal must throw a clean ObjectDisposedException,
        // not an accidental one from CancellationTokenSource.CreateLinkedTokenSource
        // observing an already-disposed _lifetime.
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
    }
}
