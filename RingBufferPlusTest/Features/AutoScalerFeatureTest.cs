using RingBufferPlus.Features;
using RingBufferPlus.ObjectValues;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RingBufferPlusTest.Features
{
    public class featureTest
    {
        private int countItemSync;
        private int countItemAsync;

        private Task<int> ItemFakeAsync(RingBufferMetric metric, CancellationToken cancellationToken)
        {
            countItemAsync++;
            var sum =
                metric.Capacity - metric.Avaliable - metric.Running +
                metric.Target +
                metric.Avaliable +
                metric.Running +
                metric.ErrorCount +
                metric.TimeoutCount +
                metric.OverloadCount +
                metric.AcquisitionCount;

            return Task.FromResult((int)sum);
        }

        private int ItemFake(RingBufferMetric metric, CancellationToken cancellationToken)
        {
            countItemSync++;
            return (int)
                (metric.Capacity - metric.Avaliable - metric.Running +
                metric.Target +
                metric.Avaliable +
                metric.Running +
                metric.ErrorCount +
                metric.TimeoutCount +
                metric.OverloadCount +
                metric.AcquisitionCount);
        }

        [Fact]
        public void Should_have_ExistFuncSync()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                ItemFake,
                CancellationToken.None);

            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.True(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.True(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public async void Should_accept_ExecuteAync_with_ItemSync()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                ItemFake,
                CancellationToken.None);

            featureTest.IncrementAcquisition();
            featureTest.IncrementErrorCount();
            featureTest.IncrementTimeout();
            featureTest.IncrementWaitCount();

            var result = await featureTest
                .ExecuteAync(new RingBufferMetric(
                        "Alias",
                        90,
                        featureTest.TimeoutCount,
                        featureTest.ErrorCount,
                        featureTest.WaitCount,
                        featureTest.AcquisitionCount,
                        0,
                        featureTest.MinAvaliable,
                        featureTest.MaxAvaliable,
                        10,
                        featureTest.BaseTime))
                .ConfigureAwait(false);

            Assert.Equal(1, countItemSync);
            Assert.Equal(0, countItemAsync);
            Assert.Equal(104, result);
            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }


        [Fact]
        public async void Should_accept_ExecuteAync_with_ItemFakeAsync()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                ItemFakeAsync,
                null,
                CancellationToken.None);

            featureTest.IncrementAcquisition();
            featureTest.IncrementErrorCount();
            featureTest.IncrementWaitCount();
            featureTest.IncrementTimeout();

            var result = await featureTest
                .ExecuteAync(new RingBufferMetric(
                        "Alias",
                        90,
                        featureTest.TimeoutCount,
                        featureTest.ErrorCount,
                        featureTest.WaitCount,
                        featureTest.AcquisitionCount,
                        0,
                        featureTest.MinAvaliable,
                        featureTest.MaxAvaliable,
                        10,
                        featureTest.BaseTime))
                .ConfigureAwait(false);


            Assert.Equal(0, countItemSync);
            Assert.Equal(1, countItemAsync);
            Assert.Equal(104, result);
            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(0, featureTest.TimeoutCount);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_have_ExistFuncAsync()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                ItemFakeAsync,
                null,
                CancellationToken.None);

            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.False(featureTest.ExistFuncSync);
            Assert.True(featureTest.ExistFuncAsync);
            Assert.True(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_have_ExistFunc()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                ItemFakeAsync,
                ItemFake,
                CancellationToken.None);

            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.True(featureTest.ExistFuncSync);
            Assert.True(featureTest.ExistFuncAsync);
            Assert.True(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_have_Initvalues()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);

            Assert.Equal(0, featureTest.AcquisitionCount);
            Assert.Equal(0, featureTest.ErrorCount);
            Assert.Equal(0, featureTest.WaitCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementAcquisition()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.DecrementAcquisition();
            Assert.Equal(-1, featureTest.AcquisitionCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), featureTest.BaseTime);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementAcquisition()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.IncrementAcquisition();
            Assert.Equal(1, featureTest.AcquisitionCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementErrorCount()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.DecrementErrorCount();
            Assert.Equal(-1, featureTest.ErrorCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementErrorCount()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.IncrementErrorCount();
            Assert.Equal(1, featureTest.ErrorCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementWaitCount()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.DecrementWaitCount();
            Assert.Equal(-1, featureTest.WaitCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementWaitCount()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.IncrementWaitCount();
            Assert.Equal(1, featureTest.WaitCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_DecrementTimeout()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.DecrementTimeout();
            Assert.Equal(-1, featureTest.TimeoutCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_IncrementTimeout()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
                CancellationToken.None);
            featureTest.IncrementTimeout();
            Assert.Equal(1, featureTest.TimeoutCount);
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }

        [Fact]
        public void Should_accept_ResetCount()
        {
            var featureTest = new AutoScalerFeature(
                10,
                1,
                TimeSpan.FromMilliseconds(100),
                null,
                null,
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
            Assert.False(featureTest.ExistFuncSync);
            Assert.False(featureTest.ExistFuncAsync);
            Assert.False(featureTest.ExistFunc);
            Assert.Equal(10, featureTest.MaxAvaliable);
            Assert.Equal(1, featureTest.MinAvaliable);
        }
    }
}