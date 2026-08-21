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
            // At currentCapacity == capacity(5), minCapacity(2): threshold = 5 - 2 + 2 = 5.
            // Boundary: the init->min check is ">=".
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 5, currentCapacity: 5,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Equal(2, result);
        }

        [Fact]
        public void EvaluateScaleDown_AtInitCapacity_MedianBelowThreshold_ReturnsNull()
        {
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 4.9, currentCapacity: 5,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_AtMaxCapacity_MedianAboveThreshold_ReturnsCapacity()
        {
            // At currentCapacity(8), capacity(5): threshold = 8 - 5 + 2 = 5.
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 6, currentCapacity: 8,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Equal(5, result);
        }

        [Fact]
        public void EvaluateScaleDown_AtMaxCapacity_MedianAtThreshold_ReturnsNull()
        {
            // Boundary: the max->init check is strictly ">", not ">=".
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 5, currentCapacity: 8,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_OffTierAboveCapacity_MedianAboveThreshold_ReturnsCapacity()
        {
            // R16 (Rodada 3): a partial scale-up (R14) can leave the buffer strictly between
            // capacity and maxCapacity (here, 7) - idleness there must still be able to trigger
            // scale-down towards capacity. The margin scales with currentCapacity (7 - 5 + 2 = 4),
            // not the fixed maxCapacity-anchored value, so it stays reachable at this capacity.
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 5, currentCapacity: 7,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Equal(5, result);
        }

        [Fact]
        public void EvaluateScaleDown_OffTierBelowCapacity_MedianAtThreshold_ReturnsMinCapacity()
        {
            // R16 (Rodada 3): a partial scale-down (R6) can leave the buffer strictly between
            // minCapacity and capacity (here, 4) - idleness there must still be able to trigger
            // scale-down towards minCapacity, using the same currentCapacity-scaled margin
            // (4 - 2 + 2 = 4).
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 4, currentCapacity: 4,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Equal(2, result);
        }

        [Fact]
        public void EvaluateScaleDown_AtMinCapacity_ReturnsNullRegardlessOfMedian()
        {
            // Already at the floor - nothing further to scale down to.
            var result = AutoScaleDecision.EvaluateScaleDown(
                median: 1000, currentCapacity: 2,
                minCapacity: 2, capacity: 5, scaleDownEnabled: true);

            Assert.Null(result);
        }

        [Fact]
        public void EvaluateScaleDown_Disabled_ReturnsNullEvenIfCapacityMatches()
        {
            var atInit = AutoScaleDecision.EvaluateScaleDown(
                median: 100, currentCapacity: 5,
                minCapacity: 2, capacity: 5, scaleDownEnabled: false);

            var atMax = AutoScaleDecision.EvaluateScaleDown(
                median: 100, currentCapacity: 8,
                minCapacity: 2, capacity: 5, scaleDownEnabled: false);

            Assert.Null(atInit);
            Assert.Null(atMax);
        }
    }
}
