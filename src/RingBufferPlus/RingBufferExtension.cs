// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************


using Microsoft.Extensions.Logging;
using RingBufferPlus.Core;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the RingBufferPlus extensions.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public static class RingBuffer<T>
    {
        /// <summary>
        /// Create a new instance to commands of RingBufferPlus.
        /// </summary>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <returns><see cref="IRingBuffer{T}"/> </returns>
        public static IRingBuffer<T> New(string buffername)
        {
            if (buffername is null)
            {
                throw new ArgumentNullException(nameof(buffername), "Buffer name is requeried");
            }
            return new RingBufferBuilder<T>(buffername,null);
        }
        /// <summary>
        /// Create a new instance to commands of RingBufferPlus.
        /// </summary>
        /// <param name="buffername">The unique name to RingBuffer.</param>
        /// <param name="loggerFactory">The logger factory to create a logger.</param>
        /// <returns><see cref="IRingBuffer{T}"/> </returns>
        public static IRingBuffer<T> New(string? buffername, ILoggerFactory loggerFactory)
        {
            if (buffername is null)
            {
                throw new ArgumentNullException(nameof(buffername), "Buffer name is requeried");
            }
            return new RingBufferBuilder<T>(buffername, loggerFactory);
        }

    }
}
