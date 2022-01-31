using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferException : Exception
    {
        private RingBufferException()
        {

        }
        internal RingBufferException(string message, Exception innerexception = null) : base(message, innerexception)
        {
        }
    }
}
