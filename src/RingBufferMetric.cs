// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the Metric of RingBufferPlus.
    /// </summary>
    public readonly struct RingBufferMetric
    {
        /// <summary>
        /// Create empty  Metric of RingBufferPlus.
        /// </summary>
        public RingBufferMetric()
        {
            MetricDate = DateTime.Now;
        }

        /// <summary>
        /// Create Metric of RingBufferPlus.
        /// </summary>
        /// <param name="source">Source tigger.</param>
        /// <param name="fromcapacity">Current capacity.</param>
        /// <param name="tocapacity">New capacity trigger.</param>
        public RingBufferMetric(SourceTrigger source, int fromcapacity, int tocapacity)
        {
            Trigger = source;
            FromCapacity = fromcapacity;
            ToCapacity = tocapacity;
            MetricDate = DateTime.Now;
        }

        /// <summary>
        /// Source tigger.
        /// </summary>
        public SourceTrigger Trigger { get; }

        /// <summary>
        /// Current capacity.
        /// </summary>
        public int FromCapacity { get; }

        /// <summary>
        /// New capacity trigger.
        /// </summary>
        public int ToCapacity { get; }

        /// <summary>
        /// Date of metric .
        /// </summary>
        public DateTime MetricDate { get;}
    }
}
