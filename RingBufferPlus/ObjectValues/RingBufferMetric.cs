using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.ObjectValues
{
    [ExcludeFromCodeCoverage]
    public class RingBufferMetric
    {
        private RingBufferMetric()
        {
        }

        internal RingBufferMetric(RingBufferState state, string alias, long timeoutcount, long errorcount, long overloadcount, long acquisitioncount, long succeededCount, TimeSpan timeexec, TimeSpan timebase)
        {
            State = state;
            Alias = alias;
            ErrorCount = errorcount;
            TimeoutCount = timeoutcount;
            OverloadCount = overloadcount;
            AcquisitionCount = acquisitioncount;
            CalculationInterval = timebase;
            AcquisitionSucceededCount = succeededCount;
            AverageSucceededExecution = timeexec;
        }

        public RingBufferState State { get; } = new RingBufferState();
        public string Alias { get; } = string.Empty;
        public long TimeoutCount { get; } = 0;
        public long ErrorCount { get; } = 0;
        public long AcquisitionCount { get; } = 0;
        public long OverloadCount { get; } = 0;
        public TimeSpan CalculationInterval { get; } = TimeSpan.Zero;
        public long AcquisitionSucceededCount { get; } = 0;
        public TimeSpan AverageSucceededExecution { get; } = TimeSpan.Zero;
    }
}
