// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Threading;

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
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns><see cref="IRingBuffer{T}"/> </returns>
        public static IRingBuffer<T> New(string buffername,CancellationToken? cancellation= null)
        {
            if (buffername is null)
            {
                throw new ArgumentNullException(nameof(buffername), "Buffer name is requeried");
            }
            return new RingBufferBuilder<T>(buffername, null, cancellation);
        }
    }
}
