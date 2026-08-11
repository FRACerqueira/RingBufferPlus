// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus builder committed to an elastic (min/init/max) capacity,
    /// producing an <see cref="IRingBufferManualScaleService{T}"/> unless <see cref="AutoScaleAcquireFault(byte)"/> is used.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferElasticBuilder<T>
    {
        /// <summary>
        /// Sets the factory (required) to create an instance in the ring buffer asynchronously.
        /// </summary>
        /// <param name="value">The handler to factory.</param>
        /// <param name="timeout">The timeout for build. Default value is 15 seconds.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer.
        /// </summary>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Default value is 10 seconds.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets to write in background (evaluation asynchronously).
        /// </summary>
        /// <param name="value">True to write in background.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> BackgroundLogger(bool value = true);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> AcquireTimeout(TimeSpan value);

        /// <summary>
        /// Sets the error handler to log errors.
        /// </summary>
        /// <param name="errorHandler">The handler to log error.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> OnError(Action<ILogger?, Exception> errorHandler);

        /// <summary>
        /// Sets acquisition/switch lock while a scale-up/scale-down operation is running.
        /// </summary>
        /// <param name="value">True to lock acquire/manual switch while scaling. Default true.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> LockWhenScaling(bool value = true);

        /// <summary>
        /// Enables autoscale (scale up) when an acquire fault occurs, and permanently removes manual switching
        /// from the built service's type (see <see cref="IRingBufferManualScaleService{T}"/>) — the two are mutually exclusive.
        /// </summary>
        /// <remarks>
        /// The scale-up process is executed when the failure threshold defined by <paramref name="numberOfFaults"/> is reached.
        /// The scale-down process is performed based on the initial, minimum or maximum capacity when the number of
        /// available buffers is greater than a value.
        /// <para>
        /// The scale-down when it is at minimum capacity is calculated using the formula: Minimum capacity - 2. If the value is less than 1 the value will be 1.
        /// </para>
        /// <para>
        /// The scale-down when it is at initial capacity is calculated using the formula: Initial capacity - Minimum capacity + 2.
        /// </para>
        /// <para>
        /// The scale-down when it is at maximum capacity is calculated using the formula: Maximum capacity - Initial capacity + 2.
        /// </para>
        /// <para>
        /// The scale-down process is executed when the calculated median of the samples collected via
        /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?)"/> reaches those values.
        /// </para>
        /// <para>
        /// Autoscale (up or down) has a timeout based on the same sampling window. When the timeout is reached, the operation is undone.
        /// </para>
        /// </remarks>
        /// <param name="numberOfFaults">Number of faults to trigger the scale-up. Default is 1 (after first fault).</param>
        /// <returns>An instance of <see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> AutoScaleAcquireFault(byte numberOfFaults = 1);

        /// <summary>
        /// Validates and generates RingBufferPlus in service mode.
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>An instance of <see cref="IRingBufferManualScaleService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">Invalid configuration of RingBuffer.</exception>
        IRingBufferManualScaleService<T> Build(CancellationToken cancellation = default);

        /// <summary>
        /// Validates and generates RingBufferPlus and warms up with full capacity ready.
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an instance of <see cref="IRingBufferManualScaleService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach its initial capacity.</exception>
        Task<IRingBufferManualScaleService<T>> BuildWarmupAsync(CancellationToken cancellation = default);
    }
}
