using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferPolicyTimeoutAccquireException : AggregateException
    {
        public RingBufferPolicyTimeoutAccquireException(string alias, string message, Exception[] exceptions) : base(message, exceptions)
        {
            Source = alias;
        }
    }
}
