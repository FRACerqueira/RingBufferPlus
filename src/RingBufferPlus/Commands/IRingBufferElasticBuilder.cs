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
        /// the batch to keep trying the remaining items instead of abandoning them.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

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
        /// Sets whether <see cref="IRingBufferManualScaleService{T}.SwitchToAsync(ScaleSwitch)"/> awaits the
        /// scale operation's completion before returning, instead of returning as soon as it is scheduled.
        /// </summary>
        /// <remarks>
        /// Does not affect <see cref="IRingBufferService{T}.AcquireAsync(CancellationToken)"/>: acquisition is
        /// never blocked by an in-progress scale operation, with or without this setting.
        /// </remarks>
        /// <param name="value">True to wait for the scale operation to finish before <c>SwitchToAsync</c> returns.
        /// Default true for this parameter - but the setting itself is off (as if this method had never been
        /// called) unless you call <c>LockWhenScaling()</c> at all.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> LockWhenScaling(bool value = true);

        /// <summary>
        /// Enables autoscale (scale up) when an acquire fault occurs, and permanently removes manual switching
        /// from the built service's type (see <see cref="IRingBufferManualScaleService{T}"/>) — the two are mutually exclusive.
        /// </summary>
        /// <remarks>
        /// The scale-up process is executed when the failure threshold defined by <paramref name="numberOfFaults"/> is reached.
        /// The scale-down process is performed based on the initial or maximum capacity when the number of
        /// available buffers is greater than a value. There is no scale-down from minimum capacity: minimum capacity is the floor.
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
        /// Scale-up has a deadline based on the factory's own per-item timeout (see
        /// <see cref="IRingBufferBuilder{T}.Factory(Func{CancellationToken, Task{T}}, TimeSpan?, byte)"/>); scale-down never
        /// waits at all. Neither direction "undoes" a partial result - a scale-up that only creates some of the
        /// needed items keeps them, and a scale-down that only finds some items idle removes just those.
        /// </para>
        /// <para>
        /// The fault counter is forgotten (reset to zero) as soon as <paramref name="numberOfFaults"/> is reached,
        /// even while already at maximum capacity - so a later scale-down always requires a fresh full batch of
        /// faults to trigger a scale-up again, never a stale leftover from before. A scale-up attempt that does
        /// not fully complete (a factory failure, or only a partial result) does not consume this budget: the very
        /// next fault retries immediately, instead of requiring an entirely new batch while already struggling.
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
