using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferTimeoutException : TimeoutException
    {
        private RingBufferTimeoutException()
        {
        }
        internal RingBufferTimeoutException(string sourcode, long elapsedTime, string message) : base(message)
        {
            ElapsedTime = elapsedTime;
            Source = sourcode;
        }

        public long ElapsedTime { get; }
    }
}
