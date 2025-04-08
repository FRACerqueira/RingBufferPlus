// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBuffer<T> : IRingBufferBuild<T>
    {
        /// <summary>
        /// Sets the initial/startup (required) capacity of the ring buffer.
        /// </summary>
        /// <param name="value">Initial capacity.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Capacity(int value);

        /// <summary>
        /// Sets the factory (required) to create an instance in the ring buffer asynchronously.
        /// </summary>
        /// <param name="value">The handler to factory.</param>
        /// <param name="timeout">The timeout for build. Default value is 15 seconds.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer. 
        /// </summary>
        /// <remarks>
        /// At each pulse, an item is acquired from the buffer for evaluation asynchronously.
        /// </remarks>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Default value is 10 seconds.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Logger(ILogger? value);

        /// <summary>
        /// Sets to write in background (evaluation asynchronously).
        /// </summary>
        /// <param name="value">True to write in background.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> BackgroundLogger(bool value = true);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <param name="delayAttempts">The time to wait between each acquisition attempt during the timeout period. Default value is 10ms.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> AcquireTimeout(TimeSpan value, TimeSpan? delayAttempts = null);

        /// <summary>
        /// Sets the error handler to log errors.
        /// </summary>
        /// <param name="errorHandler">The handler to log error.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> OnError(Action<ILogger?, Exception> errorHandler);

        /// <summary>
        /// Switches to definitions scale (min/max) and auto-scale. 
        /// </summary>
        /// <para>
        /// The <paramref name="baseTimer"/> is used as scaleup/scaledown timeout time base. When the timeout is reached the scaleup/scaledown operation is undone.
        /// </para>
        /// <param name="numberSamples">Number of samples collected. Default numberSamples is 100 (one sample per 300ms).</param>
        /// <param name="baseTimer">
        /// The <see cref="TimeSpan"/> interval to collect samples. Default value is 30 seconds (one sample per 100ms).
        /// </param>
        /// <returns><see cref="IRingBufferScaleCapacity{T}"/>.</returns>
        IRingBufferScaleCapacity<T> ScaleTimer(int? numberSamples = null, TimeSpan? baseTimer = null);
    }
}
