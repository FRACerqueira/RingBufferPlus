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
        /// <param name="value">The handler to factory. Receives a <see cref="CancellationToken"/> that
        /// fires at this call's own <paramref name="timeout"/> deadline (and on shutdown) - honoring it
        /// (passing it through to any awaited I/O, or checking it directly) is the caller's
        /// responsibility. If the factory ignores the token and keeps running after this library has
        /// already given up waiting on it, any instance it eventually produces is discarded without being
        /// disposed - a resource leak (e.g. a database connection or broker channel left open) that this
        /// library cannot detect or prevent on your behalf.</param>
        /// <param name="timeout">Per-item timeout for the factory call; also the deadline for the overall
        /// operation as <c>quantity * timeout</c> when creating several items at once (the initial warmup
        /// fill, or an autoscale-triggered scale-up) - so it bounds how long other engine operations (like
        /// reacting to the next waiting caller) wait behind it. Default is 15 seconds - inherited unchanged
        /// from a previous release, not calibrated against any particular factory. Set it deliberately
        /// based on how long your own factory call actually takes (e.g. opening a database connection or
        /// a broker channel), not the default.</param>
        /// <param name="maxConsecutiveFactoryFailures">How many consecutive factory failures (per-item
        /// timeout or exception) to tolerate within a single creation batch (the initial warmup fill, or
        /// an autoscale-triggered scale-up) before giving up on the remaining not-yet-attempted items.
        /// Resets to zero on every success, so only a true streak of failures counts, not isolated ones
        /// scattered across an otherwise healthy batch. Default is 0: the first failure gives up on the
        /// rest of the batch immediately (whatever succeeded before it is still kept) - the same behavior
        /// as before this parameter existed. Raise it if your factory has occasional, recoverable hiccups
        /// and you want the batch to keep trying the remaining items instead of abandoning them. If a
        /// tolerated failure is itself a <paramref name="timeout"/> rather than a fast exception, raising
        /// this value multiplies <paramref name="timeout"/>'s own worst-case blocking effect: the batch
        /// can now stay blocked for roughly <c>(maxConsecutiveFactoryFailures + 1) * timeout</c> before
        /// giving up on a given item, not just <paramref name="timeout"/>. Since v6.0.0 (ADR001V03), a
        /// batch's factory calls run with bounded concurrency (up to <c>maxConcurrentFactoryCalls</c>,
        /// default <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/>), not one at a time -
        /// giving up only stops items still queued behind that concurrency window, not ones already in
        /// flight when the streak is exceeded, so with both defaults a fully broken factory can still
        /// receive up to <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/> concurrent attempts
        /// before giving up, not just 1.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        IRingBufferAutoScaleBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer.
        /// </summary>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Also reused as the grace period bounding a single
        /// pooled item's <c>Dispose()</c>/<c>DisposeAsync()</c> call during shutdown or scale-down, whether
        /// or not <c>HeartBeat</c> itself is configured. Default value is 10 seconds.</param>
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
        /// Tunes the Monitor's predictive autoscale algorithm (ADR003V03): a sliding-window
        /// percentile as a demand "fair level", inflated by a safety buffer, adjusted by a
        /// linear-regression trend projected a configurable horizon ahead, clamped to
        /// [MinCapacity, MaxCapacity]. This is the lowest-priority of the four signals in
        /// ADR001V03's model (floor guard &gt; backlog-reactive &gt; manual pin &gt; Monitor) - it
        /// only acts on ticks where demand is not currently keeping pace with capacity; the window
        /// used for those ticks is <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?, int?)"/>'s
        /// own <c>numberSamples</c>, unchanged by this method.
        /// </summary>
        /// <param name="percentileP">The percentile used as the fair level, in the range (0, 1]. Default is 0.95 (p95).</param>
        /// <param name="safetyBuffer">Fractional headroom added on top of the percentile. Must be greater than or equal to 0. Default is 0.10 (10%).</param>
        /// <param name="horizon">How many sampling ticks ahead the demand trend is projected. Must be greater than or equal to 0. Default is 5.</param>
        /// <param name="deadband">The minimum difference (in items) between the computed target and the
        /// current capacity before a scale operation is dispatched - without it, the algorithm was
        /// measured to oscillate heavily under flat-but-noisy demand. Must be greater than or equal
        /// to 0. Default is 3. Also governs how long a demand change can go unnoticed during a
        /// steady period, since only a dispatched scale operation clears the sliding window - see
        /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?, int?)"/>'s
        /// own <c>numberSamples</c> parameter for what that means at the shipped defaults.</param>
        /// <returns><see cref="IRingBufferAutoScaleBuilder{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">An argument is outside its valid range - validated at <c>Build</c>/<c>BuildWarmupAsync</c> time.</exception>
        IRingBufferAutoScaleBuilder<T> MonitorTuning(double percentileP = 0.95, double safetyBuffer = 0.10, double horizon = 5, int deadband = 3);

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
