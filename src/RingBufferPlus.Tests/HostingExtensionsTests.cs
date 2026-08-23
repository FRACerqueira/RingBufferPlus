// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace RingBufferPlus.Tests
{
    public class HostingExtensionsTests
    {
        [Fact]
        public void AddRingBuffer_ShouldAddRingBufferService()
        {
            // Arrange
            var bufferName = "testBuffer";
            var services = new ServiceCollection();
            Func<IRingBufferBuilder<int>, IServiceProvider, IRingBufferService<int>> userFunc = (buffer, provider) => Mock.Of<IRingBufferService<int>>();

            // Act
            services.AddRingBuffer(bufferName, userFunc);
            var serviceProvider = services.BuildServiceProvider();
            var ringBufferService = serviceProvider.GetService<IRingBufferService<int>>();

            // Assert
            Assert.NotNull(ringBufferService);
        }

        [Fact]
        public void AddRingBuffer_ShouldAlsoRegisterAHostedServiceForWarmup()
        {
            // ADR007V03: WarmupRingBufferAsync was removed - AddRingBuffer<T> now registers an
            // IHostedService alongside the pool, so warmup happens automatically on host start
            // instead of requiring a separate opt-in call.
            var services = new ServiceCollection();
            Func<IRingBufferBuilder<int>, IServiceProvider, IRingBufferService<int>> userFunc = (buffer, provider) => Mock.Of<IRingBufferService<int>>();

            services.AddRingBuffer("testBuffer", userFunc);
            var serviceProvider = services.BuildServiceProvider();

            var hostedServices = serviceProvider.GetServices<IHostedService>();
            Assert.Contains(hostedServices, s => s.GetType().Name.StartsWith("RingBufferWarmupHostedService"));
        }

        [Fact]
        public async Task HostedService_StartAsync_WarmsUpTheNamedBufferWithItsOwnToken()
        {
            // Arrange
            var ringBufferServiceMock = new Mock<IRingBufferService<int>>();
            ringBufferServiceMock.Setup(x => x.Name).Returns("testBuffer");
            ringBufferServiceMock.Setup(x => x.WarmupAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(ringBufferServiceMock.Object);
            var serviceProvider = services.BuildServiceProvider();

            var hostedService = new RingBufferWarmupHostedService<int>(serviceProvider, "testBuffer");
            using var explicitToken = new CancellationTokenSource();

            // Act: StartAsync's own token must reach WarmupAsync directly, not a substitute.
            await hostedService.StartAsync(explicitToken.Token);

            // Assert
            ringBufferServiceMock.Verify(x => x.WarmupAsync(explicitToken.Token), Times.Once);
        }

        [Fact]
        public async Task HostedService_StartAsync_WhenBufferNotRegistered_Throws()
        {
            var services = new ServiceCollection();
            var serviceProvider = services.BuildServiceProvider();

            var hostedService = new RingBufferWarmupHostedService<int>(serviceProvider, "missingBuffer");

            await Assert.ThrowsAsync<InvalidOperationException>(() => hostedService.StartAsync(CancellationToken.None));
        }
    }
}
