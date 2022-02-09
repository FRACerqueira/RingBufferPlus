using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.ObjectValues
{
    [ExcludeFromCodeCoverage]
    public static class DefaultValues
    {
        public static readonly TimeSpan WaitTimeAvailable = TimeSpan.FromMilliseconds(5);
        public static readonly TimeSpan TimeoutAccquire = TimeSpan.FromMilliseconds(100);
        public static readonly TimeSpan IntervalHealthcheck = TimeSpan.FromMilliseconds(100);
        public static readonly TimeSpan IntervalScaler = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan IntervalReport = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan IntervalFailureState = TimeSpan.FromSeconds(5);
    }
}
