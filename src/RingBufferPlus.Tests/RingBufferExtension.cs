// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;
using Moq;

namespace RingBufferPlus.Tests
{
    public class RingBufferTests
    {
        [Fact]
        public void New_WithValidBufferName_ReturnsIRingBuffer()
        {
            // Arrange
            string bufferName = "TestBuffer";

            // Act
            var result = RingBuffer<string>.New(bufferName);

            // Assert
            Assert.NotNull(result);
            Assert.IsAssignableFrom<IRingBuffer<string>>(result);
        }

        [Fact]
        public void New_WithNullBufferName_ThrowsArgumentNullException()
        {
            // Arrange
            string? bufferName = null;

            // Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() => RingBuffer<string>.New(bufferName!));
            Assert.Equal("Buffer name is requeried (Parameter 'buffername')", exception.Message);
        }

        [Fact]
        public void New_WithValidBufferNameAndLoggerFactory_ReturnsIRingBuffer()
        {
            // Arrange
            string bufferName = "TestBuffer";
            var loggerFactory = Mock.Of<ILoggerFactory>();

            // Act
            var result = RingBuffer<string>.New(bufferName, loggerFactory);

            // Assert
            Assert.NotNull(result);
            Assert.IsAssignableFrom<IRingBuffer<string>>(result);
        }

        [Fact]
        public void New_WithNullBufferNameAndLoggerFactory_ThrowsArgumentNullException()
        {
            // Arrange
            string? bufferName = null;
            var loggerFactory = Mock.Of<ILoggerFactory>();

            // Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() => RingBuffer<string>.New(bufferName!, loggerFactory));
            Assert.Equal("Buffer name is requeried (Parameter 'buffername')", exception.Message);
        }
    }
}
