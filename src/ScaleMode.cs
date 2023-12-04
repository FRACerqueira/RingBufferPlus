// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents Scale mode of RingBufferPlus.
    /// </summary>
    public enum ScaleMode
    {
        /// <summary>
        /// Scale automatic by free-resources.
        /// </summary>
        Automatic,
        /// <summary>
        /// Scale manual by user.
        /// </summary>
        Manual,
        /// <summary>
        /// Scale manual by Master-Slave.
        /// </summary>
        Slave,
    }
}
