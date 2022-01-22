using System;

namespace RingBufferPlus.Internals
{
    internal struct ValueException<T>
    {
        public ValueException()
        {
            Value = default;
            Error = null;
        }
        public ValueException(T value, Exception ex = null) : this()
        {
            Value = value;
            Error = ex;
        }
        public Exception Error { get; }
        public T Value { get; }
    }
}
