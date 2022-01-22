using System;

namespace RingBufferPlus.ObjectValues
{
    public static class DefaultValues
    {
        public static readonly TimeSpan TimeoutFactory = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan IntervalReport = TimeSpan.FromSeconds(60);

        public static readonly TimeSpan WaitTimeAvailable = TimeSpan.FromMilliseconds(50);
        public static readonly TimeSpan TimeoutAccquire = TimeSpan.FromMilliseconds(150);
        public static readonly TimeSpan IntervalHealthcheck = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan IntervalScaler = TimeSpan.FromMilliseconds(250);
    }
}
