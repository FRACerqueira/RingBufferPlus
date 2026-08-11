// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public class AutoScaleDecisionTests
    {
        [Fact]
        public void Median_OddCount_ReturnsMiddleValue()
        {
            Assert.Equal(2, AutoScaleDecision.Median([1, 3, 2]));
        }

        [Fact]
        public void Median_EvenCount_ReturnsAverageOfTwoMiddleValues()
        {
            Assert.Equal(2.5, AutoScaleDecision.Median([4, 1, 3, 2]));
        }

        [Fact]
        public void Median_AllEqual_ReturnsThatValue()
        {
            Assert.Equal(5, AutoScaleDecision.Median([5, 5, 5, 5]));
        }

        [Fact]
        public void Median_SingleSample_ReturnsThatSample()
        {
            Assert.Equal(7, AutoScaleDecision.Median([7]));
        }

        [Fact]
        public void Median_ExtremeSpread_IsNotSkewedByOutlierBeyondTheMidpoint()
        {
            // Median is deliberately outlier-resistant, unlike a mean: the average of these
            // 5 samples would be dominated by the outlier, but the median ignores it entirely.
            Assert.Equal(3, AutoScaleDecision.Median([1, 2, 3, 4, 100_000]));
        }

        [Fact]
        public void Median_IsOrderIndependent()
        {
            Assert.Equal(AutoScaleDecision.Median([1, 2, 3]), AutoScaleDecision.Median([3, 1, 2]));
        }

        [Fact]
        public void Median_EmptyCollection_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AutoScaleDecision.Median([]));
        }

        [Fact]
        public void Median_Null_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AutoScaleDecision.Median(null!));
        }

        [Fact]
        public void EvaluateScaleDown_AtInitCapacity_MedianAtThreshold_ReturnsMinCapacity()
        {
            // Boundary: the init->min check is ">=".
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 3, isInitCapacity: true, isMaxCapacity: false,
                minCapacity: 2, capacity: 5, scaleDownInit: 3, scaleDownMax: 4);

            Assert.Equal(2, result);
        }

        [Fact]
        public void EvaluateScaleDown_AtInitCapacity_MedianBelowThreshold_ReturnsNull()
        {
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 2.9, isInitCapacity: true, isMaxCapacity: false,
                minCapacity: 2, capacity: 5, scaleDownInit: 3, scaleDownMax: 4);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_AtMaxCapacity_MedianAboveThreshold_ReturnsCapacity()
        {
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 5, isInitCapacity: false, isMaxCapacity: true,
                minCapacity: 2, capacity: 5, scaleDownInit: 3, scaleDownMax: 4);

            Assert.Equal(5, result);
        }

        [Fact]
        public void EvaluateScaleDown_AtMaxCapacity_MedianAtThreshold_ReturnsNull()
        {
            // Boundary: the max->init check is strictly ">", not ">=".
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 4, isInitCapacity: false, isMaxCapacity: true,
                minCapacity: 2, capacity: 5, scaleDownInit: 3, scaleDownMax: 4);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_NeitherAtInitNorAtMax_ReturnsNullRegardlessOfMedian()
        {
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 1000, isInitCapacity: false, isMaxCapacity: false,
                minCapacity: 2, capacity: 5, scaleDownInit: 3, scaleDownMax: 4);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_ThresholdsNotConfigured_ReturnsNullEvenIfCapacityMatches()
        {
            // scaleDownInit/scaleDownMax are null when autoscale-on-fault is disabled.
            var atInit = AutoScaleDecision.EvaluateScaleDown(
                median: 100, isInitCapacity: true, isMaxCapacity: false,
                minCapacity: 2, capacity: 5, scaleDownInit: null, scaleDownMax: null);

            var atMax = AutoScaleDecision.EvaluateScaleDown(
                median: 100, isInitCapacity: false, isMaxCapacity: true,
                minCapacity: 2, capacity: 5, scaleDownInit: null, scaleDownMax: null);

            Assert.Null(atInit);
            Assert.Null(atMax);
        }
    }
}
