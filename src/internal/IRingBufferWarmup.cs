// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the Warmup commands for Ring Buffer Plus
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    internal interface IRingBufferWarmup<T>
    {

        /// <summary>
        ///Run RingBuffer warmup with full capacity ready or reaching timeout (default 30 seconds).
        /// </summary>
        /// <param name="timeout">The timeout for full capacity ready</param>
        /// <returns>True if full capacity ready, otherwise false</returns>
        bool Warmup(TimeSpan? timeout = null);
    }
}
