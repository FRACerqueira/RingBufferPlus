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

        private IRingBufferBuilder<int> CreateBuilder() => new RingBufferBuilder<int>("TestBuffer", _loggerFactoryMock.Object);

        [Fact]
        public void Constructor_ShouldInitializeWithDefaults()
        {
            var builder = CreateBuilder();

            Assert.NotNull(builder);
        }

        [Fact]
        public void FixedCapacity_ShouldSetCapacityMinAndMax()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .FixedCapacity(7)
                .Build();

            Assert.Equal(7, service.Capacity);
            Assert.Equal(7, service.MinCapacity);
            Assert.Equal(7, service.MaxCapacity);
        }

        [Fact]
        public void ElasticCapacity_ShouldSetDistinctMinInitAndMax()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10)
                .Build();

            Assert.Equal(5, service.Capacity);
            Assert.Equal(2, service.MinCapacity);
            Assert.Equal(10, service.MaxCapacity);
        }

        [Fact]
        public void ElasticCapacity_ReturnsManualScaleService()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10)
                .Build();

            Assert.IsAssignableFrom<IRingBufferManualScaleService<int>>(service);
        }

        [Fact]
        public async Task ElasticCapacity_WithAutoScaleAcquireFault_HidesManualSwitchAtCompileTime_AndThrowsIfCastBack()
        {
            IRingBufferService<int> service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10)
                .AutoScaleAcquireFault(3)
                .Build();

            // ADR007: Build() above statically returns IRingBufferService<int> - SwitchToAsync is not
            // in scope at compile time. A caller that casts back to IRingBufferManualScaleService<int>
            // must not silently no-op; it must fail loudly (see the advisor note on the escaped-cast path).
            var escaped = Assert.IsAssignableFrom<IRingBufferManualScaleService<int>>(service);
            await Assert.ThrowsAsync<InvalidOperationException>(() => escaped.SwitchToAsync(ScaleSwitch.MaxCapacity));

            await service.DisposeAsync();
        }

        [Fact]
        public void OnError_ShouldSetErrorHandler()
        {
            Action<ILogger?, Exception> errorHandler = (logger, ex) => { };

            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .OnError(errorHandler)
                .FixedCapacity(2)
                .Build();

            var errorHandlerfield = service.GetType().GetProperty("ErrorHandler")!;
            Assert.NotNull(errorHandlerfield.GetValue(service));
        }

        [Fact]
        public void AutoScaleAcquireFault_ShouldSetAutoScaleFault()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10)
                .AutoScaleAcquireFault(5)
                .Build();

            var autoScaleFaultField = service.GetType().GetProperty("AutoScaleFault")!;
            var numberFaultField = service.GetType().GetProperty("NumberFault")!;

            Assert.True((bool)autoScaleFaultField.GetValue(service)!);
            Assert.Equal((byte)5, (byte)numberFaultField.GetValue(service)!);
        }

        [Fact]
        public void AcquireTimeout_ShouldSetAcquireTimeout()
        {
            var timeout = TimeSpan.FromSeconds(10);

            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .AcquireTimeout(timeout)
                .FixedCapacity(2)
                .Build();

            var acquireTimeoutField = service.GetType().GetProperty("AcquireTimeout")!;
            Assert.Equal(timeout, (TimeSpan)acquireTimeoutField.GetValue(service)!);
        }

        [Fact]
        public void HeartBeat_ShouldSetHeartBeat()
        {
            Action<RingBufferValue<int>> heartBeat = value => { };

            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .HeartBeat(heartBeat)
                .FixedCapacity(2)
                .Build();

            var bufferHeartBeatField = service.GetType().GetProperty("BufferHeartBeat")!;
            Assert.NotNull(bufferHeartBeatField.GetValue(service));
        }

        [Fact]
        public void Factory_ShouldSetFactory()
        {
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);

            var service = CreateBuilder()
                .Factory(factory)
                .FixedCapacity(2)
                .Build();

            var factoryField = service.GetType().GetProperty("Factory")!;
            Assert.NotNull(factoryField.GetValue(service));
        }

        [Fact]
        public void Logger_ShouldSetLogger()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .Logger(_loggerMock.Object)
                .FixedCapacity(2)
                .Build();

            var loggerField = service.GetType().GetProperty("Logger")!;
            Assert.NotNull(loggerField.GetValue(service));
        }

        [Fact]
        public void BackgroundLogger_ShouldSetBackgroundLogger()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .BackgroundLogger(true)
                .FixedCapacity(2)
                .Build();

            var backgroundLoggerField = service.GetType().GetProperty("BackgroundLogger")!;
            Assert.True((bool)backgroundLoggerField.GetValue(service)!);
        }

        [Fact]
        public void ElasticCapacity_ShouldSetSampleUnitAndBaseTime()
        {
            var service = CreateBuilder()
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(5, 2, 10, 10, TimeSpan.FromSeconds(5))
                .Build();

            var samplesBaseField = service.GetType().GetProperty("SamplesBase")!;
            var samplesCountField = service.GetType().GetProperty("SamplesCount")!;

            Assert.Equal(10, (int)samplesCountField.GetValue(service)!);
            Assert.Equal(TimeSpan.FromSeconds(5), (TimeSpan)samplesBaseField.GetValue(service)!);
        }

        [Fact]
        public async Task BuildWarmupAsync_ShouldWarmupService()
        {
            Func<CancellationToken, Task<int>> factory = token => Task.FromResult(1);

            var service = await CreateBuilder()
                .Factory(factory)
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.NotNull(service);
            Assert.True(service.IsInitCapacity);

            await service.DisposeAsync();
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenFactoryIsNull()
        {
            var builder = CreateBuilder().FixedCapacity(2);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenFixedCapacityIsLessThanTwo()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).FixedCapacity(1);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsLessThanTwo()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(5, 1, 10);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMaxCapacityIsLessThanTwo()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(2, 2, 1);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsGreaterThanMaxCapacity()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(5, 10, 5);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMinCapacityIsGreaterThanInitialCapacity()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(5, 10, 12);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenMaxCapacityIsLessThanInitialCapacity()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(10, 2, 5);

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenSampleUnitIsLessThanOne()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(5, 2, 10, 0, TimeSpan.FromSeconds(5));

            Assert.Throws<IndexOutOfRangeException>(() => builder.Build());
        }

        [Fact]
        public void ValidateBuild_ShouldThrowException_WhenSampleBaseTimeIsLessThan100ms()
        {
            var builder = CreateBuilder().Factory(_ => Task.FromResult(0)).ElasticCapacity(5, 2, 10, 10, TimeSpan.FromMilliseconds(500));

            Assert.Throws<IndexOutOfRangeException>(() => builder.Build());
        }
    }
}
