using System;

namespace RingBufferPlus.ObjectValues
{
    public struct RingBufferMetric
    {
        public RingBufferMetric()
        {
        }

        internal RingBufferMetric(string alias, int target, long timeoutcount, long errorcount, long overloadcount, long acquisitioncount, int running, int min, int max, int aval, TimeSpan tm) : this()
        {
            Alias = alias;
            Target = target;
            ErrorCount = errorcount;
            TimeoutCount = timeoutcount;
            OverloadCount = overloadcount;
            AcquisitionCount = acquisitioncount;
            Running = running;
            Minimum = min;
            Maximum = max;
            Avaliable = aval;
            CalculationInterval = tm;
        }

        public string Alias { get; } = string.Empty;
        public long TimeoutCount { get; } = 0;
        public long ErrorCount { get; } = 0;
        public long AcquisitionCount { get; } = 0;
        public long OverloadCount { get; } = 0;
        public int Running { get; } = 0;
        public int Target { get; } = 0;
        public int Avaliable { get; } = 0;
        public int Capacity => Avaliable + Running;
        public int Minimum { get; } = 0;
        public int Maximum { get; } = 0;
        public TimeSpan CalculationInterval { get; } = TimeSpan.Zero;
    }
}
