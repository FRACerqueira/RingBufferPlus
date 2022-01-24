using RingBufferPlus.Features;
using System;
using System.Threading;
using Xunit;

namespace RingBufferPlusTest.Features
{
    public class ReportFeatureTest
    {
        [Fact]
        public void Should_have_Initvalues()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);

            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementAcquisition()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.DecrementAcquisition();
            Assert.Equal(-1, featureTest.AcquisitionCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementAcquisition()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.IncrementAcquisition();
            Assert.Equal(1, featureTest.AcquisitionCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementErrorCount()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.DecrementErrorCount();
            Assert.Equal(-1, featureTest.ErrorCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementErrorCount()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.IncrementErrorCount();
            Assert.Equal(1, featureTest.ErrorCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementWaitCount()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.DecrementWaitCount();
            Assert.Equal(-1, featureTest.WaitCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementWaitCount()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.IncrementWaitCount();
            Assert.Equal(1, featureTest.WaitCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }



        [Fact]
        public void Should_accept_DecrementTimeout()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.DecrementTimeout();
            Assert.Equal(-1, featureTest.TimeoutCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementTimeout()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.IncrementTimeout();
            Assert.Equal(1, featureTest.TimeoutCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_ResetCount()
        {
            var featureTest = new ReportFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            featureTest.IncrementAcquisition();
            featureTest.IncrementErrorCount();
            featureTest.IncrementWaitCount();
            featureTest.IncrementTimeout();
            Assert.Equal(1, featureTest.AcquisitionCount);
            Assert.Equal(1, featureTest.ErrorCount);
            Assert.Equal(1, featureTest.WaitCount);
            Assert.Equal(1, featureTest.TimeoutCount);
            featureTest.ResetCount();
            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(0, featureTest.TimeoutCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }
    }
}