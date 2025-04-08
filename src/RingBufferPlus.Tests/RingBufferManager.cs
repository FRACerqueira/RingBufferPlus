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
        private readonly Mock<Func<CancellationToken, Task<int?>>> _factoryMock;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public RingBufferManagerTests()
        {
            _loggerMock = new Mock<ILogger>();
            _factoryMock = new Mock<Func<CancellationToken, Task<int?>>>();
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
                ScaleDownInit = 3,
                ScaleDownMin = 2,
                ScaleDownMax = 15,
                TriggerFault = true,
                NumberFault = 3,
                AcquireTimeout = TimeSpan.FromSeconds(1),
                AcquireDelayAttempts = TimeSpan.FromMilliseconds(100),
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
        }

        [Fact]
        public async Task AcquireAsync_ShouldReturnUnsuccessful_WhenCancelled()
        {
            // Arrange
            var manager = CreateRingBufferManager();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var result = await manager.AcquireAsync(cts.Token);

            // Assert
            Assert.False(result.Successful);
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToMinCapacity()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("TestBuffer",null);
            builder.Capacity(5);
            builder.LockAcquireWhenAutoScale();
            builder.MaxCapacity(10);
            builder.MinCapacity(2);
            builder.Factory((_) => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();

            // Act
            await service.SwitchToAsync(ScaleSwitch.MinCapacity);

            // Assert
            Assert.True(service.IsMinCapacity);
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToMaxCapacity()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("TestBuffer", null);
            builder.Capacity(5);
            builder.LockAcquireWhenAutoScale();
            builder.MaxCapacity(10);
            builder.MinCapacity(2);
            builder.Factory((_) => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();
            // Act
            await service.SwitchToAsync(ScaleSwitch.MaxCapacity);

            // Assert
            Assert.True(service.IsMaxCapacity);
        }

        [Fact]
        public async Task SwitchToAsync_ShouldScaleToInitCapacity()
        {
            // Arrange
            var builder = new RingBufferBuilder<int>("TestBuffer", null);
            builder.Capacity(5);
            builder.LockAcquireWhenAutoScale();
            builder.MaxCapacity(10);
            builder.MinCapacity(2);
            builder.Factory((_) => Task.FromResult(0));
            builder.ScaleTimer(1, TimeSpan.FromSeconds(5));
            var service = builder.Build();
            await service.WarmupAsync();

            // Act
            await service.SwitchToAsync(ScaleSwitch.InitCapacity);

            // Assert
            Assert.True(service.IsInitCapacity);
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
        }

        [Fact]
        public void Dispose_ShouldCleanupResources()
        {
            // Arrange
            var manager = CreateRingBufferManager();

            // Act
            manager.Dispose();

            // Assert
            var disposedfield = manager.GetType().GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!;
            Assert.True((bool)disposedfield.GetValue(manager)!);
        }
    }
}
