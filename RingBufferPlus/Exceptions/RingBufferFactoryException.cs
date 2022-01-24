using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferFactoryException : OperationCanceledException
    {
        public RingBufferFactoryException(string alias, string message, Exception innerexception) : base(message, innerexception)
        {
            Source = alias;
        }
    }
}
