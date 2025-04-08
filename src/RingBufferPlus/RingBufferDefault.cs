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
        /// The default delay time to attempt to acquire the buffer on failure.
        /// </summary>
        public readonly static TimeSpan AcquireDelayAttempts = TimeSpan.FromMilliseconds(10);

        /// <summary>
        /// The default sample unit for calculating autoscale.
        /// </summary>
        public readonly static int SampleUnit = 100;

        /// <summary>
        /// The default delay for scaling the capacity.
        /// </summary>
        public readonly static TimeSpan SamplesBaseTime = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The default capacity for buffer.
        /// </summary>
        public readonly static int Capacity = 2;

    }
}
