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
        /// <param name="capacity">Default capacity.</param>
        /// <param name="fromcapacity">Current capacity.</param>
        /// <param name="tocapacity"> New capacity trigger.</param>
        /// <param name="mincapacity">Minimum capacity.</param>
        /// <param name="maxcapacity">Maximum capacity.</param>
        /// <param name="freeresource">Free resource value trigger.</param>
        /// <param name="dateref">Date of metric.</param>
        public RingBufferMetric(SourceTrigger source, int fromcapacity, int tocapacity, int capacity, int mincapacity, int maxcapacity, int freeresource, DateTime dateref)
        {
            Trigger = source;
            Capacity = capacity;
            MinCapacity = mincapacity;
            MaxCapacity = maxcapacity;
            FromCapacity = fromcapacity;
            ToCapacity = tocapacity;
            FreeResource = freeresource;
            MetricDate = dateref;
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
        /// Default capacity of ring buffer.
        /// </summary>
        public int Capacity { get; }

        /// <summary>
        /// Maximum capacity of ring buffer.
        /// </summary>
        public int MaxCapacity { get; }

        /// <summary>
        /// Minimum capacity of ring buffer.
        /// </summary>
        public int MinCapacity { get; }

        /// <summary>
        /// Free resource capacity of ring buffer.
        /// </summary>
        public int FreeResource { get; }
        
        /// <summary>
        /// Date of metric .
        /// </summary>
        public DateTime MetricDate { get;}
    }
}
