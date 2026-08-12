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
        /// <param name="isInitCapacity">Whether the buffer is currently at its initial capacity.</param>
        /// <param name="isMaxCapacity">Whether the buffer is currently at its maximum capacity.</param>
        /// <param name="minCapacity">The buffer's minimum capacity (scale-down target from initial).</param>
        /// <param name="capacity">The buffer's initial capacity (scale-down target from maximum).</param>
        /// <param name="scaleDownInit">The threshold to scale down from initial to minimum capacity, or <see langword="null"/> if autoscale-on-fault is disabled.</param>
        /// <param name="scaleDownMax">The threshold to scale down from maximum to initial capacity, or <see langword="null"/> if autoscale-on-fault is disabled.</param>
        public static int? EvaluateScaleDown(
            double median,
            bool isInitCapacity,
            bool isMaxCapacity,
            int minCapacity,
            int capacity,
            int? scaleDownInit,
            int? scaleDownMax)
        {
            if (isInitCapacity && scaleDownInit.HasValue && median >= scaleDownInit.Value)
            {
                return minCapacity;
            }
            if (isMaxCapacity && scaleDownMax.HasValue && median > scaleDownMax.Value)
            {
                return capacity;
            }
            return null;
        }
    }
}
