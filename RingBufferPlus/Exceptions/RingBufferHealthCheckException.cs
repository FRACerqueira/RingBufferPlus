using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferHealthCheckException : OperationCanceledException
    {
        public RingBufferHealthCheckException(string alias, string message, Exception innerexception) : base(message, innerexception)
        {
            Source = alias;
        }
    }
}
