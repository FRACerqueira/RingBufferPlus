// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    // ADR003V03 (see doc/adr/ADR003V03-median-sample-autoscaling-algorithm.md): the Monitor's
    // autoscale algorithm. It replaces the retired median-of-idle-samples algorithm
    // (AutoScaleDecision) with a different formula: a sliding-window percentile plus a safety
    // buffer as a "fair level", adjusted by a linear-regression trend projected a configurable
    // horizon ahead, then clamped to [min, max].
    //
    // This formula is ported from the validated simulation in
    // benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs
    // (PercentileRegressionDecision). See that file and the ADR for the evidence behind it and
    // its defaults - convergence, over-provisioning, and oscillation numbers across four
    // synthetic scenarios.
    //
    // Wired into RingBufferManager's engine loop via ProcessTick (the Monitor role, ADR001V03).
    // ProcessTick feeds it CurrentCapacity minus idle plus _waitingCount as demand samples -
    // never an idle-derived proxy. Idle is clamped at zero, so it's blind to unmet demand: under
    // sustained saturation, that would silently flatten the regression slope right where scaling
    // up matters most, reproducing the same failure mode this algorithm was chosen to fix.
    //
    // Deadband and clearing the window mid-episode are call-site concerns, not part of this
    // calculation - the same simulation validated both, and both are part of ADR003V03's
    // decision. See the simulation's SimulatePercentile, which applies both around its call to
    // EvaluateTarget rather than inside it. ProcessTick implements both here too.
    internal static class AutoScaleMonitor
    {
        /// <summary>
        /// Computes the given percentile of <paramref name="samples"/> via linear interpolation
        /// between the two nearest ranks (the standard "linear" method).
        /// </summary>
        /// <param name="samples">
        /// Demand samples - the concurrent load observed at each tick. These may exceed the
        /// buffer's current capacity (unmet demand is not clamped away); do not pass an
        /// idle-derived or capacity-clamped proxy here, see the class remarks.
        /// </param>
        /// <param name="p">The percentile to compute, in [0, 1] (e.g. 0.95 for p95).</param>
        public static double Percentile(IReadOnlyList<int> samples, double p)
        {
            if (samples is null || samples.Count == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(samples));
            }

            var sorted = samples.OrderBy(x => x).ToArray();
            var rank = p * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            if (lo == hi)
            {
                return sorted[lo];
            }
            var frac = rank - lo;
            return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
        }

        /// <summary>
        /// Least-squares slope of <paramref name="samples"/> against their index (a simple linear
        /// trend: positive means demand is rising tick-over-tick, negative means it is falling).
        /// </summary>
        /// <param name="samples">Demand samples - see <see cref="Percentile"/>'s remarks.</param>
        public static double Slope(IReadOnlyList<int> samples)
        {
            if (samples is null || samples.Count == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(samples));
            }

            var n = samples.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (var i = 0; i < n; i++)
            {
                sumX += i;
                sumY += samples[i];
                sumXY += i * samples[i];
                sumXX += (double)i * i;
            }
            var denom = (n * sumXX) - (sumX * sumX);
            if (denom == 0)
            {
                return 0;
            }
            return ((n * sumXY) - (sumX * sumY)) / denom;
        }

        /// <summary>
        /// Computes the Monitor's predictive target capacity: the <paramref name="percentileP"/>
        /// percentile of <paramref name="samples"/>, inflated by <paramref name="safetyBuffer"/>
        /// as a "fair level", adjusted by the linear trend projected <paramref name="horizon"/>
        /// ticks ahead, clamped to <c>[<paramref name="min"/>, <paramref name="max"/>]</c>.
        /// </summary>
        /// <param name="samples">Demand samples - see <see cref="Percentile"/>'s remarks.</param>
        /// <param name="percentileP">The percentile used as the fair level (ADR003V03 default: 0.95).</param>
        /// <param name="safetyBuffer">Fractional headroom added on top of the percentile (default: 0.10).</param>
        /// <param name="horizon">How many ticks ahead the trend is projected (default: 5).</param>
        /// <param name="min">The buffer's minimum capacity.</param>
        /// <param name="max">The buffer's maximum capacity.</param>
        public static int EvaluateTarget(IReadOnlyList<int> samples, double percentileP, double safetyBuffer, double horizon, int min, int max)
        {
            var p = Percentile(samples, percentileP);
            var fair = p * (1 + safetyBuffer);
            var slope = Slope(samples);
            var target = fair + (slope * horizon);
            var clamped = Math.Clamp(target, min, max);
            return (int)Math.Ceiling(clamped);
        }
    }
}
