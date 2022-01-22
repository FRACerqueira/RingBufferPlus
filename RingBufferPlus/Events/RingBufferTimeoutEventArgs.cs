using RingBufferPlus.ObjectValues;
using System;

namespace RingBufferPlus.Events
{
    public class RingBufferTimeoutEventArgs : EventArgs
    {

        private RingBufferTimeoutEventArgs()
        {
        }
        internal RingBufferTimeoutEventArgs(string alias, string source, long elapsedTime, long timeout, RingBufferMetric metricInfo)
        {
            Alias = alias;
            Source = source;
            Metric = metricInfo;
            ElapsedTime = elapsedTime;
            Timeout = timeout;
        }
        public string Source { get; }
        public long ElapsedTime { get; }
        public long Timeout { get; }
        public string Alias { get; }
        public RingBufferMetric Metric { get; }
    }
}
