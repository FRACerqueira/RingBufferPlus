// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus service that can be manually switched between capacities.
    /// </summary>
    /// <remarks>
    /// This contract is only available when the buffer was built with <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?)"/>
    /// and without <see cref="IRingBufferElasticBuilder{T}.AutoScaleAcquireFault(byte)"/>. When autoscale-on-fault is enabled,
    /// or the buffer has a fixed capacity, manual switching is not exposed at the type level (see ADR007).
    /// </remarks>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferManualScaleService<T> : IRingBufferService<T>
    {
        /// <summary>
        /// Try to manually switch the current capacity.
        /// </summary>
        /// <param name="value">New scale capacity.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous operation. The result is <see langword="false"/>
        /// when the buffer is already at the requested capacity or a scale operation is already running; otherwise <see langword="true"/>.
        /// </returns>
        Task<bool> SwitchToAsync(ScaleSwitch value);
    }
}
