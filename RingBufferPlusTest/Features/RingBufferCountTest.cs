using RingBufferPlus.Features;
using System;
using Xunit;

namespace RingBufferPlusTest.Features
{
    public class RingBufferCountTest
    {
        [Fact]
        public void Should_have_DefautValue()
        {
            var rc = new RingBufferCount();
            Assert.Equal(0, rc.AcquisitionCount);
            Assert.Equal(0, rc.AcquisitionSucceeded);
            Assert.Equal(TimeSpan.Zero, rc.AverageSucceeded);
            Assert.Equal(0, rc.ErrorCount);
            Assert.Equal(0, rc.TimeoutCount);
            Assert.Equal(0, rc.WaitCount);
        }

        [Fact]
        public void Should_IncrementAcquisition_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementAcquisition();
            Assert.Equal(1, rc.AcquisitionCount);
        }

        [Fact]
        public void Should_IncrementAcquisitionSucceeded_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementAcquisitionSucceeded(TimeSpan.FromMilliseconds(10));
            Assert.Equal(1, rc.AcquisitionSucceeded);
        }
        [Fact]
        public void Should_AverageSucceeded_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementAcquisitionSucceeded(TimeSpan.FromMilliseconds(20));
            rc.IncrementAcquisitionSucceeded(TimeSpan.FromMilliseconds(10));
            Assert.Equal(TimeSpan.FromMilliseconds(15), rc.AverageSucceeded);
        }

        [Fact]
        public void Should_IncrementErrorCount_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementErrorCount();
            Assert.Equal(1, rc.ErrorCount);
        }


        [Fact]
        public void Should_IncrementTimeout_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementTimeout();
            Assert.Equal(1, rc.TimeoutCount);
        }

        [Fact]
        public void Should_IncrementWaitCountt_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementWaitCount();
            Assert.Equal(1, rc.WaitCount);
        }
        [Fact]
        public void Should_Reset_ok()
        {
            var rc = new RingBufferCount();
            rc.IncrementAcquisition();
            rc.IncrementAcquisitionSucceeded(TimeSpan.FromMilliseconds(10));
            rc.IncrementErrorCount();
            rc.IncrementTimeout();
            rc.IncrementWaitCount();
            Assert.Equal(1, rc.AcquisitionCount);
            Assert.Equal(1, rc.AcquisitionSucceeded);
            Assert.NotEqual(TimeSpan.Zero, rc.AverageSucceeded);
            Assert.Equal(1, rc.ErrorCount);
            Assert.Equal(1, rc.TimeoutCount);
            Assert.Equal(1, rc.WaitCount);
            rc.ResetCount();
            Assert.Equal(0, rc.AcquisitionCount);
            Assert.Equal(0, rc.AcquisitionSucceeded);
            Assert.Equal(TimeSpan.Zero, rc.AverageSucceeded);
            Assert.Equal(0, rc.ErrorCount);
            Assert.Equal(0, rc.TimeoutCount);
            Assert.Equal(0, rc.WaitCount);
        }
    }
}
