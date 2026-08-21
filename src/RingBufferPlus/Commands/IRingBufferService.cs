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
        /// The Current capacity of the RingBuffer.
        /// </summary>
        int CurrentCapacity { get; }

        /// <summary>
        /// Is Maximum capacity of the RingBuffer.
        /// </summary>
        bool IsMaxCapacity { get; }

        /// <summary>
        /// Is Minimum capacity of the RingBuffer.
        /// </summary>
        bool IsMinCapacity { get; }

        /// <summary>
        /// Is Initial capacity of the RingBuffer.
        /// </summary>
        bool IsInitCapacity { get; }

        /// <summary>
        /// The Value Maximum capacity of the RingBuffer.
        /// </summary>
        int MaxCapacity { get; }

        /// <summary>
        /// The Value Minimum capacity of the RingBuffer.
        /// </summary>
        int MinCapacity { get; }

        /// <summary>
        /// The Value Initial capacity of the RingBuffer.
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
        /// It is recommended to use this method in the initialization of the application.
        /// If a previous call to this method failed, calling it again retries the attempt from
        /// scratch instead of rethrowing the same cached failure - a transient factory failure
        /// (e.g. a database or broker not yet accepting connections at startup) does not
        /// permanently disable the instance. This retry only happens when <c>WarmupAsync</c> is
        /// called explicitly again; <see cref="AcquireAsync(CancellationToken)"/> and
        /// <c>SwitchToAsync</c> only observe the outcome of the most recent attempt and never
        /// trigger a retry on their own.
        /// </remarks>
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach initial capacity.</exception>
        /// <exception cref="ObjectDisposedException">The instance was already disposed.</exception>
        Task WarmupAsync(CancellationToken cancellation = default);
    }
}
