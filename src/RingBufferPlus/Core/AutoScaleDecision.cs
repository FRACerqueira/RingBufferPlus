// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    // ADR003: the median-of-samples autoscaling algorithm itself is not changing in v5 - there
    // is no evidence it needs to. What was missing was the ability to test the calculation and
    // the threshold decision in isolation from RingBufferManager's async engine loop. This type
    // is that isolation: a pure, static, side-effect-free unit with no dependency on Channels,
    // CancellationTokens, or the manager's mutable state.
    //
    // R16 (Rodada 3): EvaluateScaleDown originally only ever fired at the exact initial or
    // maximum capacity, using a safety margin fixed at build time from Min/Init/MaxCapacity (e.g.
    // "idle > maxCapacity - capacity + 2"). A partial scale-up/down (R14/R6) can leave the buffer
    // off-tier - anywhere strictly between those exact values - where that fixed margin can be
    // mathematically unreachable (an idle count can never exceed currentCapacity itself), so the
    // buffer got stuck there forever regardless of how idle it became. The margin below is scaled
    // to currentCapacity instead of a fixed tier, so an off-tier position is no longer stuck
    // *relative to where it landed*; it reduces to the exact original formula whenever
    // currentCapacity is exactly at the initial or maximum capacity. This does not, by itself,
    // fix the margin formula's own pre-existing degenerate case: when initial capacity is 2 (the
    // minimum legal value), "current - capacity + 2" equals currentCapacity itself, which the
    // median (bounded by currentCapacity) can never exceed - scale-down from above initial
    // capacity is unreachable regardless of position, exactly as with the original fixed formula.
    // Registered, not fixed here (out of R16's original scope) - see the viability report.
    internal static class AutoScaleDecision
    {
        /// <summary>
        /// Computes the median of the given samples.
        /// </summary>
        /// <param name="samples">At least one sample.</param>
        /// <exception cref="ArgumentException"><paramref name="samples"/> is empty.</exception>
        public static double Median(IReadOnlyCollection<int> samples)
        {
            if (samples is null || samples.Count == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(samples));
            }

            var sorted = samples.OrderBy(x => x).ToArray();
            var length = sorted.Length;
            if (length % 2 == 0)
            {
                var pos = length / 2;
                return (sorted[pos - 1] + sorted[pos]) / 2.0;
            }
            var mid = (length + 1) / 2;
            return sorted[mid - 1];
        }

        /// <summary>
        /// Decides whether the sampled median crosses a scale-down threshold, and if so, the
        /// capacity to scale down to. Returns <see langword="null"/> when no scale-down applies.
        /// </summary>
        /// <param name="median">The median computed by <see cref="Median(IReadOnlyCollection{int})"/>.</param>
        /// <param name="currentCapacity">The buffer's current capacity - not just its exact initial or maximum capacity, but any capacity it may have landed on, including an off-tier value left by a partial scale operation.</param>
        /// <param name="minCapacity">The buffer's minimum capacity (scale-down target when at or below initial capacity).</param>
        /// <param name="capacity">The buffer's initial capacity (scale-down target when above initial capacity).</param>
        /// <param name="scaleDownEnabled">Whether autoscale-on-fault (and therefore scale-down) is enabled.</param>
        public static int? EvaluateScaleDown(
            double median,
            int currentCapacity,
            int minCapacity,
            int capacity,
            bool scaleDownEnabled)
        {
            if (!scaleDownEnabled)
            {
                return null;
            }

            if (currentCapacity > capacity)
            {
                // Reduces to the original "maxCapacity - capacity + 2" whenever currentCapacity
                // is exactly maxCapacity.
                if (median > currentCapacity - capacity + 2)
                {
                    return capacity;
                }
                return null;
            }
            if (currentCapacity > minCapacity)
            {
                // Reduces to the original "capacity - minCapacity + 2" whenever currentCapacity
                // is exactly capacity.
                if (median >= currentCapacity - minCapacity + 2)
                {
                    return minCapacity;
                }
            }
            return null;
        }
    }
}
