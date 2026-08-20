// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus builder committed to an elastic capacity with autoscale-on-fault enabled.
    /// </summary>
    /// <remarks>
    /// Manual switching is not available from this point on: <see cref="Build(CancellationToken)"/> and
    /// <see cref="BuildWarmupAsync(CancellationToken)"/> return a plain <see cref="IRingBufferService{T}"/>,
    /// not an <see cref="IRingBufferManualScaleService{T}"/> (see ADR007).
    /// </remarks>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferAutoScaleBuilder<T>
    {
        /// <summary>
        /// Sets the factory (required) to create an instance in the ring buffer asynchronously.
        /// </summary>
        /// <param name="value">The handler to factory.</param>
        /// <param name="timeout">Per-item timeout for the factory call; also the deadline for the overall
        /// operation as <c>quantity * timeout</c> when creating several items at once (the initial warmup
        /// fill, or an autoscale-triggered scale-up) - so it bounds how long other engine operations (like
        /// reacting to the next acquire fault) wait behind it. Default is 15 seconds - inherited unchanged
        /// from a previous release, not calibrated against any particular factory. Set it deliberately
        /// based on how long your own factory call actually takes (e.g. opening a database connection or
        /// a broker channel), not the default.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer.
        /// </summary>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Default value is 10 seconds.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets to write in background (evaluation asynchronously).
        /// </summary>
        /// <param name="value">True to write in background.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> BackgroundLogger(bool value = true);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> AcquireTimeout(TimeSpan value);

        /// <summary>
        /// Sets the error handler to log errors.
        /// </summary>
        /// <param name="errorHandler">The handler to log error.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> OnError(Action<ILogger?, Exception> errorHandler);

        /// <summary>
        /// Validates and generates RingBufferPlus in service mode.
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>An instance of <see cref="IRingBufferService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">Invalid configuration of RingBuffer.</exception>
        IRingBufferService<T> Build(CancellationToken cancellation = default);

        /// <summary>
        /// Validates and generates RingBufferPlus and warms up with full capacity ready.
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an instance of <see cref="IRingBufferService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach its initial capacity.</exception>
        Task<IRingBufferService<T>> BuildWarmupAsync(CancellationToken cancellation = default);
    }
}
