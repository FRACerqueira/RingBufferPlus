using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferAccquireException : OperationCanceledException
    {
        public RingBufferAccquireException(string alias, string message, Exception innerexception) : base(message, innerexception)
        {
            Source = alias;
        }
    }
}
