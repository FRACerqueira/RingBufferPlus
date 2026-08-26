// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the default values for the ring buffer.
    /// </summary>
    public static class RingBufferDefault
    {
        /// <summary>
        /// The default timeout for the factory handler.
        /// </summary>
        public readonly static TimeSpan FactoryTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The default timeout for the buffer health checks. Also reused as the default grace period
        /// bounding a single pooled item's own <c>Dispose()</c>/<c>DisposeAsync()</c> call during shutdown
        /// or scale-down - this applies whether or not a <c>HeartBeat</c> callback is configured at all.
        /// </summary>
        public readonly static TimeSpan PulseHeartBeat = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The default timeout for acquiring the buffer.
        /// </summary>
        public readonly static TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The default sample unit for calculating autoscale.
        /// </summary>
        public readonly static int SampleUnit = 100;

        /// <summary>
        /// The default delay for scaling the capacity.
        /// </summary>
        public readonly static TimeSpan SamplesBaseTime = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The default maximum number of concurrent factory calls when creating several items at
        /// once (initial warmup, or a scale-up). Limits how many creation attempts (e.g. database
        /// or broker connections) hit a struggling downstream at the same time.
        /// </summary>
        public readonly static int MaxConcurrentFactoryCalls = 4;

        /// <summary>
        /// Internal placeholder capacity used before <c>FixedCapacity</c>/<c>ElasticCapacity</c> is called on a
        /// builder. Not a reachable default: every path to <c>Build</c>/<c>BuildWarmupAsync</c> requires calling
        /// one of those methods first, which always overwrites this value - a built service never actually
        /// runs with this capacity.
        /// </summary>
        public readonly static int Capacity = 2;

        /// <summary>
        /// The default percentile used by the Monitor's predictive autoscale algorithm
        /// as the demand "fair level" - p95.
        /// </summary>
        public readonly static double MonitorPercentileP = 0.95;

        /// <summary>
        /// The default fractional headroom the Monitor adds on top of the percentile "fair level"
        /// - 10%.
        /// </summary>
        public readonly static double MonitorSafetyBuffer = 0.10;

        /// <summary>
        /// The default number of sampling ticks the Monitor's linear-regression demand trend is
        /// projected ahead.
        /// </summary>
        public readonly static double MonitorHorizon = 5;

        /// <summary>
        /// The minimum difference between the Monitor's computed target and the current capacity
        /// before it triggers a scale operation. Without this deadband, the algorithm oscillates
        /// under flat but noisy demand (measured).
        /// </summary>
        public readonly static int MonitorDeadband = 3;

    }
}
