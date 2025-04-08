// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public class RingBufferBuilderTests
    {
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ILogger> _loggerMock;

        public RingBufferBuilderTests()
        {
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerMock = new Mock<ILogger>();
            _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_loggerMock.Object);
        }

        [Fact]
        public void Constructor_ShouldInitializeWithDefaults()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            Assert.NotNull(builder);
        }

        [Fact]
        public void MaxCapacity_ShouldSetMaxCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.MaxCapacity(100);
            builder.Factory((_) => Task.FromResult(0));
            var service = builder.Build();
            Assert.Equal(100, service.MaxCapacity);
        }

        [Fact]
        public void MinCapacity_ShouldSetMinCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            builder.Capacity(5);
            builder.MinCapacity(2);
            builder.Factory((_) => Task.FromResult(0));
            var service = builder.Build();
            Assert.Equal(2, service.MinCapacity);
        }

        [Fact]
        public void OnError_ShouldSetErrorHandler()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Action<ILogger?, Exception> errorHandler = (logger, ex) => { };

            builder.OnError(errorHandler);
            builder.Factory((_) => Task.FromResult(0));
            var service = builder.Build();
            var errorHandlerfield = service.GetType().GetProperty("ErrorHandler")!;
            Assert.NotNull(errorHandlerfield.GetValue(service));
        }

        [Fact]
        public void AutoScaleAcquireFault_ShouldSetTriggerFault()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.AutoScaleAcquireFault(5);
            builder.Factory((_) => Task.FromResult(0));
            var service = builder.Build();
            var triggerFaultField = service.GetType().GetProperty("TriggerFault")!;
            var numberFaultField = service.GetType().GetProperty("NumberFault")!;

            Assert.True((bool)triggerFaultField.GetValue(service)!);
            Assert.Equal(5, (byte)numberFaultField.GetValue(service)!);
        }

        [Fact]
        public void AcquireTimeout_ShouldSetAcquireTimeout()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            var timeout = TimeSpan.FromSeconds(10);

            builder.AcquireTimeout(timeout);
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            var acquireTimeoutField = service.GetType().GetProperty("AcquireTimeout")!;
            Assert.Equal(timeout, (TimeSpan)acquireTimeoutField.GetValue(service)!);
        }

        [Fact]
        public void HeartBeat_ShouldSetHeartBeat()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Action<RingBufferValue<int>> heartBeat = value => { };

            builder.HeartBeat(heartBeat);
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            var bufferHeartBeatField = service.GetType().GetProperty("BufferHeartBeat")!;
            Assert.NotNull(bufferHeartBeatField);
        }

        [Fact]
        public void Capacity_ShouldSetCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.Capacity(50);
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            Assert.Equal(50, service.Capacity);
        }

        [Fact]
        public void Factory_ShouldSetFactory()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);

            builder.Factory(factory);

            var service = builder.Build();
            var factoryField = service.GetType().GetProperty("Factory")!;
            Assert.NotNull(factoryField);
        }

        [Fact]
        public void Logger_ShouldSetLogger()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.Logger(_loggerMock.Object);
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            var loggerField = service.GetType().GetProperty("Logger")!;
            Assert.NotNull(loggerField);
        }

        [Fact]
        public void BackgroundLogger_ShouldSetBackgroundLogger()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.BackgroundLogger(true);
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            var backgroundLoggerField = service.GetType().GetProperty("BackgroundLogger")!;
            Assert.True((bool)backgroundLoggerField.GetValue(service)!);
        }

        [Fact]
        public void ScaleTimer_ShouldSetScaleTimer()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            builder.ScaleTimer(10, TimeSpan.FromSeconds(5));
            builder.Factory((_) => Task.FromResult(0));

            var service = builder.Build();
            var samplesBaseField = service.GetType().GetProperty("SamplesBase")!;
            var samplesCountField = service.GetType().GetProperty("SamplesCount")!;

            Assert.Equal(10, (int)samplesCountField.GetValue(service)!);
            Assert.Equal(TimeSpan.FromSeconds(5), (TimeSpan)samplesBaseField.GetValue(service)!);
        }

        [Fact]
        public async Task BuildWarmupAsync_ShouldWarmupService()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);

            var service = await builder.BuildWarmupAsync();

            Assert.NotNull(service);
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenFactoryIsNull()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenCapacityIsLessThanTwo()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.Capacity(1);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsLessThanTwo()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.MinCapacity(1);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMaxCapacityIsLessThanTwo()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.MaxCapacity(1);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsGreaterThanMaxCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.MinCapacity(10);
            builder.MaxCapacity(5);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsGreaterThanInitialCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.Capacity(5);
            builder.MinCapacity(10);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMaxCapacityIsLessThanInitialCapacity()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.Capacity(10);
            builder.MaxCapacity(5);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenSampleUnitIsLessThanOne()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.ScaleTimer(0, TimeSpan.FromSeconds(5));

            Assert.Throws<IndexOutOfRangeException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenSampleBaseTimeIsLessThan100ms()
        {
            var builder = new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);
            builder.Factory(factory);
            builder.ScaleTimer(10, TimeSpan.FromMilliseconds(500));

            Assert.Throws<IndexOutOfRangeException>(() => builder.Build());
        }
    }
}
