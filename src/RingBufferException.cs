// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the Exception by RingBufferPlus.
    /// </summary>
    [Serializable]
    public class RingBufferException : Exception
    {

        /// <summary>
        /// Name of ring buffer
        /// </summary>
        public string? NameRingBuffer { get; }

        /// <summary>
        /// Create empty RingBufferException..
        /// </summary>
        public RingBufferException()
        {
        }

        /// <summary>
        /// Create RingBufferException.
        /// </summary>
        /// <param name="name">Name of ring buffer.</param>
        /// <param name="message">The message exception.</param>
        public RingBufferException(string name, string message)
            : base(message)
        {
            NameRingBuffer = name;
        }

        /// <summary>
        /// Create RingBufferException with inner exception.
        /// </summary>
        /// <param name="name">Name of ring buffer.</param>
        /// <param name="message">The message exception.</param>
        /// <param name="inner">The inner exception.</param>
        public RingBufferException(string name, string message, Exception inner)
            : base(message, inner)
        {
            NameRingBuffer = name;
        }
    }
}