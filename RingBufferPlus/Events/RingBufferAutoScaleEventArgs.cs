using RingBufferPlus.ObjectValues;
using System;

namespace RingBufferPlus.Events
{
    public class RingBufferAutoScaleEventArgs : EventArgs
    {
        private RingBufferAutoScaleEventArgs()
        {
        }

        internal RingBufferAutoScaleEventArgs(string alias, int oldvalue, int newvalue, RingBufferMetric metric)
        {
            Alias = alias;
            OldCapacity = oldvalue;
            NewCapacity = newvalue;
            Metric = metric;
        }
        public string Alias { get; }
        public int OldCapacity { get; }
        public int NewCapacity { get; }
        public RingBufferMetric Metric { get; }
    }
}
