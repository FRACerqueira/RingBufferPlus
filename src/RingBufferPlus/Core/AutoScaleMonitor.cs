// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    // ADR003V03 (see doc/adr/ADR003V03-median-sample-autoscaling-algorithm.md): replaces the
    // median-of-idle-samples algorithm (AutoScaleDecision) with a sliding-window percentile +
    // safety buffer "fair level", adjusted by a linear-regression trend projected a configurable
    // horizon ahead, clamped to [min, max]. Ported from the validated simulation in
    // benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs
    // (PercentileRegressionDecision) - see that file and the ADR for the evidence (convergence,
    // over-provisioning, and oscillation numbers across four synthetic scenarios) behind this
    // formula and its defaults.
    //
    // NOT YET WIRED into RingBufferManager's engine loop. This is the Monitor role's pure
    // calculation core (ADR001V03), validated here in isolation - the same staged approach
    // AutoScaleDecision.cs itself used. Wiring it in requires the Orquestrador's floor-guard /
    // backlog-reactive / pin / Monitor signal-priority model (ADR001V03) to exist first: this
    // algorithm's own simulation feeds it unclamped demand (including the portion that exceeds
    // current capacity), which only the backlog-reactive signal (waiting callers) can supply
    // truthfully - AutoScaleDecision's existing idle-count sampling cannot, because idle is
    // clamped at zero and therefore blind to unmet demand. Wiring this against a clamped proxy
    // (e.g. current-capacity-minus-idle) would silently flatten the regression slope under
    // sustained saturation - exactly the plateau where scaling up matters most - reproducing a
    // different-shaped version of the failure mode this algorithm was chosen to fix. Do not wire
    // it ahead of that plumbing.
    //
    // Deadband and window-reset-while-a-reactive-episode-is-in-flight (also validated by the same
    // simulation, and part of ADR003V03's decision) are call-site concerns, not part of this
    // calculation - see the simulation's SimulatePercentile, which applies both around its call to
    // EvaluateTarget rather than inside it. They belong to whatever wires this into the engine
    // loop, not to this pure unit.
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
