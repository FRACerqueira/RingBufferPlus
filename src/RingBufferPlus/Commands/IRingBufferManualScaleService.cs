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
    /// This contract is only available when the buffer was built with <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?, int?)"/>
    /// and without <see cref="IRingBufferElasticBuilder{T}.AutoScaleAcquireFault(byte)"/>. When autoscale-on-fault is enabled,
    /// or the buffer has a fixed capacity, manual switching is not exposed at the type level (see ADR007).
    /// </remarks>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferManualScaleService<T> : IRingBufferService<T>
    {
        /// <summary>
        /// Try to manually switch the current capacity.
        /// </summary>
        /// <remarks>
        /// If the implicit warmup this method triggers previously failed and has not been retried via an
        /// explicit call to <see cref="IRingBufferService{T}.WarmupAsync(CancellationToken)"/>, this rethrows
        /// that same failure.
        /// </remarks>
        /// <param name="value">New scale capacity.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous operation. The result is <see langword="false"/>
        /// when the buffer is already at the requested capacity. Otherwise it is <see langword="true"/> — except when
        /// <see cref="IRingBufferElasticBuilder{T}.LockWhenScaling(bool)"/> is enabled, in which case the result instead
        /// reflects whether the scale operation fully reached the target capacity (<see langword="true"/>) or only
        /// partially completed before its own timeout (<see langword="false"/>). A partial result is <b>not</b> undone:
        /// whatever capacity was actually gained or removed before the timeout is kept, and <see cref="IRingBufferService{T}.CurrentCapacity"/>
        /// reflects it. A call made while another scale operation is already in flight is queued behind it, not rejected.
        /// </returns>
        /// <exception cref="InvalidOperationException">The buffer has a fixed capacity, or autoscale-on-fault is
        /// enabled - reachable only by escaping the type system (casting to <see cref="IRingBufferManualScaleService{T}"/>
        /// from a plain <see cref="IRingBufferService{T}"/> reference), since the type-level exclusivity (ADR007)
        /// otherwise prevents calling this at all.</exception>
        /// <exception cref="ObjectDisposedException">The instance was already disposed.</exception>
        Task<bool> SwitchToAsync(ScaleSwitch value);
    }
}
