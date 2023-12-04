// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents Scale type of RingBufferPlus.
    /// </summary>
    internal enum ScaleType
    {
        /// <summary>
        /// Current ScaleUnit, Renew an item in the buffer
        /// </summary>
        ReNew,
        /// <summary>
        /// ScaleUnit to minimal capacity.
        /// </summary>
        ToMinCapacity,
        /// <summary>
        /// ScaleUnit to maximum capacity.
        /// </summary>
        ToMaxCapacity,
        /// <summary>
        /// ScaleUnit to default capacity.
        /// </summary>
        ToDefaultCapacity
    }
}
