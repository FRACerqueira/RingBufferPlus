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
    public interface IRingBufferService<T> : IDisposable
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
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="ValueTask{TResult}"/> representing the asynchronous operation, with a <see cref="RingBufferValue{T}"/> result.</returns>
        ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default);

        /// <summary>
        /// Try manually switch scale.
        /// </summary>
        /// <remarks>
        /// Manually change scale will always return false if autoscale is enabled.
        /// </remarks>
        /// <param name="value">New scale capacity.</param>
        /// <returns> A <see cref="Task"/> representing the asynchronous operation with result (true/false).
        /// <para>
        /// When there is already a running scale process, false is returned otherwise true
        /// </para>
        /// </returns>
        Task<bool> SwitchToAsync(ScaleSwitch value);

        /// <summary>
        /// Warms up with full capacity ready.
        /// <remarks>
        /// It is recommended to use this method in the initialization of the application.
        /// </remarks>
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach initial capacity(TimeSpan Timeout of ScaleTimer)</exception>
        Task WarmupAsync(CancellationToken cancellation = default);
    }
}
