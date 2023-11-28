// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Threading;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the salve commands to RingBufferPlus service.
    /// </summary>
    public interface IRingBufferSwith
    {
        /// <summary>
        /// Swith to new capacity in slave RingBuffer
        /// </summary>
        /// <param name="scaleMode"></param>
        /// <returns>True if scale changed, otherwise false</returns>
        bool SwithTo(ScaleMode scaleMode);

    }
}
