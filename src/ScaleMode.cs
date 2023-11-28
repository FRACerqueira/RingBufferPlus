// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents Scale Mode of RingBufferPlus.
    /// </summary>
    public enum ScaleMode
    {
        /// <summary>
        /// Current Scale 
        /// </summary>
        None,
        /// <summary>
        /// Scale to minimal capacity.
        /// </summary>
        ToMinCapacity,
        /// <summary>
        /// Scale to maximum capacity.
        /// </summary>
        ToMaxCapacity,
        /// <summary>
        /// Scale to default capacity.
        /// </summary>
        ToDefaultCapacity
    }
}
