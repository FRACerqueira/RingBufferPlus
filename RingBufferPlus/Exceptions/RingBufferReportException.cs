using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.Exceptions
{
    [ExcludeFromCodeCoverage]
    public class RingBufferReportException : OperationCanceledException
    {
        public RingBufferReportException(string alias, string message, Exception innerexception) : base(message, innerexception)
        {
            Source = alias;
        }
    }
}
