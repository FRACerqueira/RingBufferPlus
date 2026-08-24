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
            services.AddKeyedSingleton("testBuffer", ringBufferServiceMock.Object);
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

        // ---------------------------------------------------------------------
        // Round 1 (Resiliência, v6 pre-release audit): the hosted service used to look up its
        // own buffer via GetServices<IRingBufferService<T>>().FirstOrDefault(x => x.Name ==
        // buffername) - which forces the DI container to construct EVERY registered
        // IRingBufferService<T>, not just the named one, because FirstOrDefault must enumerate
        // in registration order until it finds a match. "bad" is registered before "good" here
        // specifically so that looking up "good" would have to pass through "bad"'s factory
        // first under the old implementation, faulting an otherwise-healthy buffer's startup.
        // ---------------------------------------------------------------------

        [Fact]
        public async Task HostedService_StartAsync_ForOneBuffer_DoesNotConstructAnUnrelatedBufferOfTheSameType()
        {
            var badFactoryInvoked = false;
            var goodMock = new Mock<IRingBufferService<int>>();
            goodMock.Setup(x => x.Name).Returns("good");
            goodMock.Setup(x => x.WarmupAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddRingBuffer<int>("bad", (_, _) =>
            {
                badFactoryInvoked = true;
                throw new InvalidOperationException("bad buffer is intentionally broken");
            });
            services.AddRingBuffer<int>("good", (_, _) => goodMock.Object);

            var serviceProvider = services.BuildServiceProvider();
            var goodHostedService = new RingBufferWarmupHostedService<int>(serviceProvider, "good");

            await goodHostedService.StartAsync(CancellationToken.None);

            Assert.False(badFactoryInvoked, "Starting one buffer's hosted service must not construct an unrelated, differently-named buffer of the same T.");
            goodMock.Verify(x => x.WarmupAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
