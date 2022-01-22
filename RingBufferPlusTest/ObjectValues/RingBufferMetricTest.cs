using RingBufferPlus.ObjectValues;
using System;
using Xunit;

namespace RingBufferPlusTest.ObjectValues
{
    public class RingBufferMetricTest
    {
        [Fact]
        public void Should_have_CorrectValues()
        {
            var featureTest = new RingBufferMetric("Alias", 1, 2, 3, 4, 5, 6, 7, 8, 9, TimeSpan.FromMilliseconds(10));
            Assert.Equal(7, featureTest.Minimum);
            Assert.Equal(8, featureTest.Maximum);
            Assert.Equal(9, featureTest.Avaliable);
            Assert.Equal(5, featureTest.AcquisitionCount);
            Assert.Equal(15, featureTest.Capacity);
            Assert.Equal("Alias", featureTest.Alias);
            Assert.Equal(TimeSpan.FromMilliseconds(10), featureTest.CalculationInterval);
            Assert.Equal(2, featureTest.TimeoutCount);
            Assert.Equal(3, featureTest.ErrorCount);
            Assert.Equal(4, featureTest.OverloadCount);
            Assert.Equal(6, featureTest.Running);
            Assert.Equal(1, featureTest.Target);
        }
    }
}
