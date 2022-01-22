using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferFatalException : InvalidOperationException
    {
        private RingBufferFatalException()
        {
        }
        internal RingBufferFatalException(string sourcode, string message) : base(message)
        {
            Source = sourcode;
        }
    }
}
