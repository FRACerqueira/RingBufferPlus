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
        /// The default timeout for the buffer health checks.
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
        /// Internal placeholder capacity used before <c>FixedCapacity</c>/<c>ElasticCapacity</c> is called on a
        /// builder. Not a reachable default: every path to <c>Build</c>/<c>BuildWarmupAsync</c> requires calling
        /// one of those methods first, which always overwrites this value - a built service never actually
        /// runs with this capacity.
        /// </summary>
        public readonly static int Capacity = 2;

    }
}
