// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Acceptance tests for the v6.0.0 Monitor algorithm (ADR003V03), ported and validated in
// isolation before being wired into the engine - see the class remarks on AutoScaleMonitor and
// benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs, whose
// PercentileRegressionDecision this file's expectations are ported from.

using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public class AutoScaleMonitorTests
    {
        [Fact]
        public void Percentile_P50_OddCount_ReturnsMiddleValue()
        {
            Assert.Equal(2, AutoScaleMonitor.Percentile([1, 3, 2], 0.5));
        }

        [Fact]
        public void Percentile_P100_ReturnsMaximum()
        {
            Assert.Equal(9, AutoScaleMonitor.Percentile([1, 9, 3, 2], 1.0));
        }

        [Fact]
        public void Percentile_P0_ReturnsMinimum()
        {
            Assert.Equal(1, AutoScaleMonitor.Percentile([5, 1, 9, 3], 0.0));
        }

        [Fact]
        public void Percentile_InterpolatesBetweenTheTwoNearestRanks()
        {
            // 5 sorted samples [1,2,3,4,5], p=0.9 -> rank = 0.9*4 = 3.6 -> interpolate between
            // index 3 (4) and index 4 (5): 4 + 0.6*(5-4) = 4.6.
            Assert.Equal(4.6, AutoScaleMonitor.Percentile([3, 1, 5, 2, 4], 0.9), precision: 10);
        }

        [Fact]
        public void Percentile_SingleSample_ReturnsThatSample()
        {
            Assert.Equal(7, AutoScaleMonitor.Percentile([7], 0.95));
        }

        [Fact]
        public void Percentile_EmptyCollection_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AutoScaleMonitor.Percentile([], 0.95));
        }

        [Fact]
        public void Percentile_Null_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AutoScaleMonitor.Percentile(null!, 0.95));
        }

        [Fact]
        public void Slope_ConstantSamples_IsZero()
        {
            Assert.Equal(0, AutoScaleMonitor.Slope([5, 5, 5, 5]));
        }

        [Fact]
        public void Slope_StrictlyIncreasingByOne_IsOne()
        {
            Assert.Equal(1, AutoScaleMonitor.Slope([1, 2, 3, 4, 5]), precision: 10);
        }

        [Fact]
        public void Slope_StrictlyDecreasingByTwo_IsNegativeTwo()
        {
            Assert.Equal(-2, AutoScaleMonitor.Slope([10, 8, 6, 4, 2]), precision: 10);
        }

        [Fact]
        public void Slope_SingleSample_IsZero()
        {
            // n == 1 makes the least-squares denominator zero - defined as flat, not a divide-by-zero.
            Assert.Equal(0, AutoScaleMonitor.Slope([42]));
        }

        [Fact]
        public void Slope_EmptyCollection_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AutoScaleMonitor.Slope([]));
        }

        [Fact]
        public void EvaluateTarget_FlatDemand_ReturnsPercentilePlusBufferRoundedUp()
        {
            // All samples = 10, p95 = 10, fair = 10 * 1.10 = 11, slope = 0 -> target = 11.
            var target = AutoScaleMonitor.EvaluateTarget(
                Enumerable.Repeat(10, 20).ToArray(), percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, min: 2, max: 32);

            Assert.Equal(11, target);
        }

        [Fact]
        public void EvaluateTarget_RisingTrend_ProjectsAboveTheFairLevelAlone()
        {
            // Strictly increasing demand (slope +1): the regression term must push the target
            // above the fair level (percentile+buffer) computed from that same window alone,
            // isolating the trend's own contribution rather than comparing across two different
            // windows.
            var rising = Enumerable.Range(0, 20).Select(i => 10 + i).ToArray(); // 10..29
            var fairOnly = (int)Math.Ceiling(AutoScaleMonitor.Percentile(rising, 0.95) * 1.10);
            var target = AutoScaleMonitor.EvaluateTarget(rising, percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, min: 2, max: 64);

            Assert.True(target > fairOnly, $"Expected the positive trend to push the target ({target}) above the fair level alone ({fairOnly}).");
        }

        [Fact]
        public void EvaluateTarget_FallingTrend_ProjectsBelowTheFairLevelAlone()
        {
            var falling = Enumerable.Range(0, 20).Select(i => 29 - i).ToArray(); // 29..10, slope -1
            var fairOnly = (int)Math.Ceiling(AutoScaleMonitor.Percentile(falling, 0.95) * 1.10);
            var target = AutoScaleMonitor.EvaluateTarget(falling, percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, min: 2, max: 64);

            Assert.True(target < fairOnly, $"Expected the negative trend to pull the target ({target}) below the fair level alone ({fairOnly}).");
        }

        [Fact]
        public void EvaluateTarget_IsClampedToMax_EvenWhenTheFormulaWouldExceedIt()
        {
            var target = AutoScaleMonitor.EvaluateTarget(
                Enumerable.Repeat(1000, 20).ToArray(), percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, min: 2, max: 32);

            Assert.Equal(32, target);
        }

        [Fact]
        public void EvaluateTarget_IsClampedToMin_EvenWhenTheFormulaWouldFallBelowIt()
        {
            // A steep falling trend projected `horizon` ticks ahead can go negative; the floor
            // must still hold. p95=17.75, fair=19.525, slope=-5, target=19.525-25=-5.475 - well
            // below the floor without the clamp.
            var falling = Enumerable.Range(0, 10).Select(i => 20 - (i * 5)).ToArray();
            var target = AutoScaleMonitor.EvaluateTarget(
                falling, percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, min: 4, max: 32);

            Assert.Equal(4, target);
        }
    }
}
