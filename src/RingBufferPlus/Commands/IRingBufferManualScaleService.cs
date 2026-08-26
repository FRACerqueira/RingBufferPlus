// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus service that can be manually pinned to a capacity.
    /// </summary>
    /// <remarks>
    /// This contract is only available when the buffer was built with
    /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int?, int?, TimeSpan?, int?)"/> - a
    /// fixed-capacity buffer does not expose manual switching at the type level.
    /// The floor guard, backlog-reactive signal, and Monitor are always active for an elastic pool.
    /// <see cref="SwitchToAsync(ScaleSwitch, TimeSpan)"/> does not replace them - it is a temporary pin
    /// that substitutes for the Monitor's own predictive output for the given duration. The floor guard
    /// and backlog-reactive signal are never suppressed by an active pin.
    /// </remarks>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferManualScaleService<T> : IRingBufferService<T>
    {
        /// <summary>
        /// Pins the buffer to a capacity for a required duration, overriding the Monitor's own
        /// predictive output for that long.
        /// </summary>
        /// <remarks>
        /// If the implicit warmup this method triggers previously failed and has not been retried via an
        /// explicit call to <see cref="IRingBufferService{T}.WarmupAsync(CancellationToken)"/>, this rethrows
        /// that same failure.
        /// </remarks>
        /// <param name="value">New scale capacity.</param>
        /// <param name="pinDuration">How long this pin overrides the Monitor's own predictive output for.
        /// Required, with no default, and must be greater than <see cref="TimeSpan.Zero"/> - earlier
        /// versions shipped two silent, unbounded traps here, and this rejects a third one (a pin nobody
        /// remembers is still active) by forcing an explicit, bounded value.
        /// This does not affect the floor guard or the backlog-reactive signal, which are never suppressed
        /// by an active pin. Only the Monitor's own scale-up/scale-down evaluation is held off, for this
        /// long, in favor of whatever capacity this call (or the floor guard/backlog-reactive signal acting
        /// during the pin) leaves the buffer at.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
        /// The result is <see langword="false"/> if the buffer is already at the requested capacity (no pin
        /// is set in that case). Otherwise it is <see langword="true"/> - unless
        /// <see cref="IRingBufferElasticBuilder{T}.LockWhenScaling(bool)"/> is enabled, in which case it
        /// instead reflects whether the scale operation fully reached the target (<see langword="true"/>) or
        /// only partially completed before its own timeout (<see langword="false"/>).
        /// A partial result is <b>not</b> undone: whatever capacity was actually gained or removed before
        /// the timeout is kept, and <see cref="IRingBufferService{T}.CurrentCapacity"/> reflects it.
        /// A call made while another scale operation - manual, floor-guard, backlog-reactive, or
        /// Monitor-driven - is already in flight is rejected, not queued: only one such batch runs at a
        /// time.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pinDuration"/> is not greater than <see cref="TimeSpan.Zero"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer has a fixed capacity. This is normally
        /// impossible to call at all - it's only reachable by casting a plain
        /// <see cref="IRingBufferService{T}"/> reference to <see cref="IRingBufferManualScaleService{T}"/>,
        /// bypassing the type system.</exception>
        /// <exception cref="ObjectDisposedException">The instance was already disposed.</exception>
        Task<bool> SwitchToAsync(ScaleSwitch value, TimeSpan pinDuration);
    }
}
