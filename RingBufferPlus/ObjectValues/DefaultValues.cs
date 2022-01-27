using System;

namespace RingBufferPlus.ObjectValues
{
    public static class DefaultValues
    {
        public static readonly TimeSpan WaitTimeAvailable = TimeSpan.FromMilliseconds(10);
        public static readonly TimeSpan TimeoutAccquire = TimeSpan.FromMilliseconds(100);
        public static readonly TimeSpan IntervalHealthcheck = TimeSpan.FromMilliseconds(250);
        public static readonly TimeSpan IntervalScaler = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan IntervalReport = TimeSpan.FromSeconds(60);
    }
}
