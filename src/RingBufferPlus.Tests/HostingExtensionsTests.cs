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
            Func<IRingBuffer<int>, IServiceProvider, IRingBufferService<int>> userFunc = (buffer, provider) => Mock.Of<IRingBufferService<int>>();

            // Act
            services.AddRingBuffer(bufferName, userFunc);
            var serviceProvider = services.BuildServiceProvider();
            var ringBufferService = serviceProvider.GetService<IRingBufferService<int>>();

            // Assert
            Assert.NotNull(ringBufferService);
        }

        [Fact]
        public async Task WarmupRingBufferAsync_ShouldWarmupRingBuffer()
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

            // Act
            await hostMock.Object.WarmupRingBufferAsync<int>("testBuffer");

            // Assert
            ringBufferServiceMock.Verify(x => x.WarmupAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

    }
}
