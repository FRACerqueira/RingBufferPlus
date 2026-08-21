// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using RingBufferPlus.Core;
using Microsoft.Extensions.Logging;
using Moq;

namespace RingBufferPlus.Tests
{
    public class RingBufferManagerTests
    {
        private readonly Mock<ILogger> _loggerMock;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public RingBufferManagerTests()
        {
            _loggerMock = new Mock<ILogger>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        private RingBufferManager<int> CreateRingBufferManager()
        {
            return new RingBufferManager<int>(_cancellationTokenSource.Token)
            {
                Name = "TestBuffer",
                Capacity = 10,
                MinCapacity = 5,
                MaxCapacity = 20,
                FactoryTimeout = TimeSpan.FromSeconds(1),
                PulseHeartBeat = TimeSpan.FromSeconds(1),
                SamplesBase = TimeSpan.FromSeconds(1),
                SamplesCount = 5,
                AutoScaleFault = true,
                NumberFault = 3,
                AcquireTimeout = TimeSpan.FromSeconds(1),
                Logger = _loggerMock.Object,
                BackgroundLogger = true,
                Factory = (_) => Task.FromResult(1)
            };
        }

        [Fact]
        public async Task AcquireAsync_ShouldReturnBufferValue_WhenBufferIsAvailable()
        {
            // Arrange
            var manager = CreateRingBufferManager();
            await manager.WarmupAsync();

            // Act
            var result = await manager.AcquireAsync();

            // Assert
            Assert.True(result.Successful);
            Assert.Equal(1, result.Current);

            await manager.DisposeAsync();
        }

        [Fact]
        public async Task AcquireAsync_ShouldThrow_WhenAlreadyCancelled()
        {
            // Arrange
            var manager = CreateRingBufferManager();
            await manager.WarmupAsync();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert: a caller-supplied, already-cancelled token is a genuine
            // cancellation, not an "unsuccessful acquire" - it must propagate.
            await Assert.ThrowsAsync<TaskCanceledException>(() => manager.AcquireAsync(cts.Token).AsTask());

            await manager.DisposeAsync();
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToMinCapacity()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("TestBuffer", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Act
            await service.SwitchToAsync(ScaleSwitch.MinCapacity);

            // Assert
            Assert.True(service.IsMinCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToMaxCapacity()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("TestBuffer", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Act
            await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // Assert
            Assert.True(service.IsMaxCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToInitCapacity()
        {
            // Arrange
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("TestBuffer", null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();
            await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // Act
            await service.SwitchToAsync(ScaleSwitch.InitCapacity);

            // Assert
            Assert.True(service.IsInitCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task WarmupAsync_ShouldInitializeBuffer()
        {
            // Arrange
            var manager = CreateRingBufferManager();

            // Act
            await manager.WarmupAsync();

            // Assert
            Assert.True(manager.IsInitCapacity);

            await manager.DisposeAsync();
        }

        [Fact]
        public async Task DisposeAsync_ShouldMarkDisposed()
        {
            // Arrange
            var manager = CreateRingBufferManager();
            await manager.WarmupAsync();

            // Act
            await manager.DisposeAsync();

            // Assert
            var disposedfield = manager.GetType().GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True((bool)disposedfield.GetValue(manager)!);
        }
    }
}
