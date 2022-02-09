using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferTimeoutException : Exception
    {
        private RingBufferTimeoutException()
        {
        }

        internal RingBufferTimeoutException(string sourcode, long elapsedTime, string message) : base(message)
        {
            ElapsedTime = elapsedTime;
            Alias = sourcode;
        }

        public string Alias { get; }

        public long ElapsedTime { get; }
    }
}
