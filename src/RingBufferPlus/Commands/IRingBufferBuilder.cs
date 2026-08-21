// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the entry point to configure and build a RingBufferPlus instance.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferBuilder<T>
    {
        /// <summary>
        /// Sets the factory (required) to create an instance in the ring buffer asynchronously.
        /// </summary>
        /// <param name="value">The handler to factory.</param>
        /// <param name="timeout">Per-item timeout for the factory call; also the deadline for the overall
        /// operation as <c>quantity * timeout</c> when creating several items at once (the initial warmup
        /// fill, or a scale-up) - so it bounds how long other engine operations wait behind it. Default is
        /// 15 seconds - inherited unchanged from a previous release, not calibrated against any particular
        /// factory. Set it deliberately based on how long your own factory call actually takes (e.g.
        /// opening a database connection or a broker channel), not the default.</param>
        /// <param name="maxConsecutiveFactoryFailures">How many consecutive factory failures (per-item
        /// timeout or exception) to tolerate within a single creation batch (the initial warmup fill, or
        /// a scale-up) before giving up on the remaining not-yet-attempted items. Resets to zero on every
        /// success, so only a true streak of failures counts, not isolated ones scattered across an
        /// otherwise healthy batch. Default is 0: the first failure gives up on the rest of the batch
        /// immediately (whatever succeeded before it is still kept) - the same behavior as before this
        /// parameter existed. Raise it if your factory has occasional, recoverable hiccups and you want
        /// the batch to keep trying the remaining items instead of abandoning them. If a tolerated
        /// failure is itself a <paramref name="timeout"/> rather than a fast exception, raising this
        /// value multiplies <paramref name="timeout"/>'s own worst-case blocking effect: the batch can
        /// now stay blocked for roughly <c>(maxConsecutiveFactoryFailures + 1) * timeout</c> before
        /// giving up on a given item, not just <paramref name="timeout"/>.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer.
        /// </summary>
        /// <remarks>
        /// At each pulse, an item is acquired from the buffer for evaluation asynchronously.
        /// </remarks>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Default value is 10 seconds.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets to write in background (evaluation asynchronously).
        /// </summary>
        /// <param name="value">True to write in background.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> BackgroundLogger(bool value = true);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> AcquireTimeout(TimeSpan value);

        /// <summary>
        /// Sets the error handler to log errors.
        /// </summary>
        /// <param name="errorHandler">The handler to log error.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> OnError(Action<ILogger?, Exception> errorHandler);

        /// <summary>
        /// Sets a fixed capacity for the ring buffer: no autoscale, no manual switch, no min/max range.
        /// </summary>
        /// <param name="value">The fixed capacity. Value must be greater than or equal to 2.</param>
        /// <returns>An instance of <see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> FixedCapacity(int value);

        /// <summary>
        /// Sets an elastic capacity for the ring buffer, enabling manual switching between
        /// <paramref name="minCapacity"/>, <paramref name="initialCapacity"/> and <paramref name="maxCapacity"/>
        /// via <see cref="IRingBufferManualScaleService{T}.SwitchToAsync(ScaleSwitch)"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="baseTimer"/>/<paramref name="numberSamples"/> configure the scale-<b>down</b> sampling
        /// cadence only (used by autoscale-on-fault's evaluation) - they do not bound a scale-up or scale-down
        /// operation's own deadline. A scale-up's deadline is <c>quantity * FactoryTimeout</c> (see
        /// <see cref="IRingBufferBuilder{T}.Factory(Func{CancellationToken, Task{T}}, TimeSpan?, byte)"/>); a
        /// scale-down never waits at all. Neither direction undoes a partial result on timeout - whatever
        /// capacity was actually gained or removed is kept.
        /// </remarks>
        /// <param name="initialCapacity">Initial/startup capacity. Value must be greater than or equal to <paramref name="minCapacity"/> and less than or equal to <paramref name="maxCapacity"/>.</param>
        /// <param name="minCapacity">The minimal buffer capacity. Value must be greater than or equal to 2.</param>
        /// <param name="maxCapacity">The maximum buffer capacity. Value must be greater than or equal to <paramref name="minCapacity"/>.</param>
        /// <param name="numberSamples">Number of samples collected. Default is 100 (one sample per 300ms).</param>
        /// <param name="baseTimer">The <see cref="TimeSpan"/> interval to collect samples. Default value is 30 seconds (one sample per 300ms).</param>
        /// <returns>An instance of <see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> ElasticCapacity(int initialCapacity, int minCapacity, int maxCapacity, int? numberSamples = null, TimeSpan? baseTimer = null);
    }
}
