// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus builder committed to an elastic (min/init/max) capacity,
    /// producing an <see cref="IRingBufferManualScaleService{T}"/>.
    /// </summary>
    /// <remarks>
    /// The floor guard, backlog-reactive signal, and Monitor
    /// are always active for any elastic pool - there is no separate "automatic vs. manual" mode to
    /// choose between as in v4/v5 (<see cref="IRingBufferManualScaleService{T}.SwitchToAsync(ScaleSwitch, TimeSpan)"/>
    /// is a temporary pin over the Monitor's own output, not a mutually exclusive alternative to it).
    /// </remarks>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferElasticBuilder<T>
    {
        /// <summary>
        /// Sets the factory (required) to create an instance in the ring buffer asynchronously.
        /// </summary>
        /// <param name="value">The handler that creates an instance. It receives a <see cref="CancellationToken"/>
        /// that fires at the <paramref name="timeout"/> deadline (and on shutdown) - pass it through to any
        /// I/O you await, or check it yourself. If the factory ignores the token and keeps running anyway,
        /// any instance it eventually produces is discarded without being disposed - a resource leak (e.g.
        /// an open database connection or broker channel) that this library cannot detect or prevent for
        /// you.</param>
        /// <param name="timeout">Timeout for one factory call. When creating several items at once (the
        /// initial warmup fill, or a scale-up), the whole batch's deadline is <c>quantity * timeout</c>,
        /// which bounds how long other engine operations wait behind it. Default is 15 seconds - a generic
        /// value, not tuned for any specific factory. Set it based on how long your own factory call
        /// actually takes (e.g. opening a database connection or broker channel).</param>
        /// <param name="maxConsecutiveFactoryFailures">How many consecutive factory failures (timeouts or
        /// exceptions) to tolerate in one creation batch (the initial warmup fill, or a scale-up) before
        /// giving up on the remaining items. A success resets the count to zero, so only a real streak
        /// counts, not a few failures scattered across an otherwise healthy batch.
        /// Default is 0: the first failure ends the batch immediately, keeping whatever already succeeded.
        /// Raise it if your factory has occasional, recoverable hiccups and you want the batch to keep
        /// trying the rest.
        /// If a tolerated failure is itself a <paramref name="timeout"/> rather than a fast exception,
        /// raising this value also raises the worst-case wait for a single item: it can block for roughly
        /// <c>(maxConsecutiveFactoryFailures + 1) * timeout</c> instead of just <paramref name="timeout"/>.
        /// Factory calls also run with bounded concurrency (<c>maxConcurrentFactoryCalls</c>, default
        /// <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/>) rather than one at a time. Giving up
        /// only stops items still waiting for a slot - calls already in flight keep running. So with
        /// default settings, a fully broken factory can still make up to
        /// <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/> concurrent attempts, not just
        /// one.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer: periodically inspects a live item's health.
        /// </summary>
        /// <remarks>
        /// At each <paramref name="pulse"/>, an item is acquired from the buffer and handed to
        /// <paramref name="value"/> for inspection - the framework owns acquiring and returning it,
        /// not your callback: there is no disposable object handed to you to misuse.
        /// Returning <see langword="false"/> discards the item and creates a replacement in its
        /// place, through the same path <see cref="RingBufferValue{T}.Invalidate"/> uses for any
        /// other caller; returning <see langword="true"/> returns it to the pool normally.
        /// </remarks>
        /// <param name="value">Receives the live item; return <see langword="false"/> if it is
        /// unhealthy (discard and replace) or <see langword="true"/> if it is still fine to keep.
        /// A callback that blocks past <paramref name="pulse"/> is treated as a timeout regardless
        /// of what it eventually returns.</param>
        /// <param name="pulse">The Heart Beat Interval. Also reused as the grace period bounding a single
        /// pooled item's <c>Dispose()</c>/<c>DisposeAsync()</c> call during shutdown or scale-down, whether
        /// or not <c>HeartBeat</c> itself is configured. Default value is 10 seconds.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> HeartBeat(Func<T, bool> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> AcquireTimeout(TimeSpan value);

        /// <summary>
        /// Sets the error handler, invoked inline instead of this buffer's own Error-level logging
        /// whenever it would otherwise log an error internally.
        /// </summary>
        /// <param name="errorHandler">The handler to invoke with the error, called synchronously and
        /// inline - no background queue. It replaces the logger configured via
        /// <see cref="Logger(ILogger?)"/> at Error level, rather than adding to it: once this is set,
        /// that <see cref="ILogger"/> stops receiving Error-level messages from this buffer (every
        /// other level is unaffected). To surface the error through both your own sink and the
        /// logger, call the logger yourself from inside this handler - that is not done for
        /// you.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> OnError(Action<Exception> errorHandler);

        /// <summary>
        /// Sets whether <see cref="IRingBufferManualScaleService{T}.SwitchToAsync(ScaleSwitch, TimeSpan)"/> awaits the
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
        /// Tunes the Monitor, the lowest-priority of the four scale signals (floor guard &gt;
        /// backlog-reactive &gt; manual pin &gt; Monitor). It computes a target capacity from a
        /// percentile of recent demand (a "fair level"), adds a safety buffer, and adjusts for a
        /// projected trend - always clamped to [MinCapacity, MaxCapacity]. It only acts when demand
        /// isn't keeping pace with capacity, using the sample window set by
        /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int?, int?, TimeSpan?, int?)"/>'s
        /// own <c>numberSamples</c>, which this method does not change.
        /// </summary>
        /// <param name="percentileP">The percentile used as the fair level, in the range (0, 1]. Default is 0.95 (p95).</param>
        /// <param name="safetyBuffer">Fractional headroom added on top of the percentile. Must be greater than or equal to 0. Default is 0.10 (10%).</param>
        /// <param name="horizon">How many sampling ticks ahead the demand trend is projected. Must be greater than or equal to 0. Default is 5.</param>
        /// <param name="deadband">The minimum difference, in items, between the computed target and the
        /// current capacity before a scale operation is dispatched. Must be 0 or greater; default is 3.
        /// Without a deadband, the algorithm was measured to oscillate heavily under flat, noisy demand.
        /// It also affects how long a demand change can go unnoticed during a steady period, since only a
        /// dispatched scale operation clears the sliding window - see
        /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int?, int?, TimeSpan?, int?)"/>'s
        /// <c>baseTimer</c> parameter for how long that can last.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">An argument is outside its valid range - validated at <c>Build</c>/<c>BuildWarmupAsync</c> time.</exception>
        IRingBufferElasticBuilder<T> MonitorTuning(double percentileP = 0.95, double safetyBuffer = 0.10, double horizon = 5, int deadband = 3);

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
