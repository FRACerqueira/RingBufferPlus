// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the commands to RingBufferPlus service.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferService<T> : IAsyncDisposable
    {
        /// <summary>
        /// Unique name of the RingBuffer.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Current capacity of the buffer.
        /// </summary>
        int CurrentCapacity { get; }

        /// <summary>
        /// True when the buffer is at its maximum capacity.
        /// </summary>
        bool IsMaxCapacity { get; }

        /// <summary>
        /// True when the buffer is at its minimum capacity.
        /// </summary>
        bool IsMinCapacity { get; }

        /// <summary>
        /// True when the buffer is at its initial (startup) capacity.
        /// </summary>
        bool IsInitCapacity { get; }

        /// <summary>
        /// The buffer's configured maximum capacity.
        /// </summary>
        int MaxCapacity { get; }

        /// <summary>
        /// The buffer's configured minimum capacity.
        /// </summary>
        int MinCapacity { get; }

        /// <summary>
        /// The buffer's configured initial capacity.
        /// </summary>
        int Capacity { get; }

        /// <summary>
        /// Try to acquire a value from the buffer.
        /// Will wait for a buffer item to become available or timeout (default 5 seconds).
        /// <remarks>
        /// A timeout (or disposal while waiting) does not throw - it returns a <see cref="RingBufferValue{T}"/>
        /// with <see cref="RingBufferValue{T}.Successful"/> set to <see langword="false"/>. Only the caller's
        /// own <paramref name="cancellation"/> firing rethrows <see cref="OperationCanceledException"/>.
        /// Calling this after <see cref="IAsyncDisposable.DisposeAsync"/> throws <see cref="ObjectDisposedException"/>.
        /// If the implicit warmup this method triggers previously failed and has not been retried via an
        /// explicit call to <see cref="WarmupAsync(CancellationToken)"/>, this rethrows that same failure.
        /// </remarks>
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="ValueTask{TResult}"/> representing the asynchronous operation, with a <see cref="RingBufferValue{T}"/> result.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was triggered by the caller.</exception>
        /// <exception cref="ObjectDisposedException">The instance was already disposed.</exception>
        ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default);

        /// <summary>
        /// Warms up with full capacity ready.
        /// <remarks>
        /// Recommended for use during application startup.
        /// If a previous call failed, calling this again retries from scratch instead of rethrowing the
        /// cached failure - so a transient factory failure (e.g. a database or broker not yet accepting
        /// connections at startup) doesn't permanently disable the instance. Only an explicit call to
        /// <c>WarmupAsync</c> retries; <see cref="AcquireAsync(CancellationToken)"/> and
        /// <c>SwitchToAsync</c> just observe the outcome of the most recent attempt and never retry on
        /// their own.
        /// </remarks>
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach initial capacity.</exception>
        /// <exception cref="ObjectDisposedException">The instance was already disposed.</exception>
        Task WarmupAsync(CancellationToken cancellation = default);
    }
}
