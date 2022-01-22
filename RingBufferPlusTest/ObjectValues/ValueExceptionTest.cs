using RingBufferPlus.Internals;
using System;
using Xunit;

namespace RingBufferPlusTest.ObjectValues
{
    public class ValueExceptionTest
    {
        [Fact]
        public void Should_have_CorrectValues()
        {
            var featureTest = new ValueException<int>(1, new Exception("Teste"));
            Assert.Equal(1, featureTest.Value);
            Assert.NotNull(featureTest.Error);
            Assert.Equal("Teste", featureTest.Error.Message);
        }

        [Fact]
        public void Should_have_CorrectDefautValues()
        {
            var featureTest = new ValueException<int>(10);
            Assert.Equal(10, featureTest.Value);
            Assert.Null(featureTest.Error);
        }
    }
}
