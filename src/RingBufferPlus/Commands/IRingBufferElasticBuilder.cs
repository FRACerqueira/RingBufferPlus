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
        /// <param name="value">The handler to factory. Receives a <see cref="CancellationToken"/> that
        /// fires at this call's own <paramref name="timeout"/> deadline (and on shutdown) - honoring it
        /// (passing it through to any awaited I/O, or checking it directly) is the caller's
        /// responsibility. If the factory ignores the token and keeps running after this library has
        /// already given up waiting on it, any instance it eventually produces is discarded without being
        /// disposed - a resource leak (e.g. a database connection or broker channel left open) that this
        /// library cannot detect or prevent on your behalf.</param>
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
        /// giving up on a given item, not just <paramref name="timeout"/>. Since v6.0.0 (ADR001V03), a
        /// batch's factory calls run with bounded concurrency (up to <c>maxConcurrentFactoryCalls</c>,
        /// default <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/>), not one at a time -
        /// giving up only stops items still queued behind that concurrency window, not ones already in
        /// flight when the streak is exceeded, so with both defaults a fully broken factory can still
        /// receive up to <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/> concurrent attempts
        /// before giving up, not just 1.</param>
        /// <returns><see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer.
        /// </summary>
        /// <param name="value">The <see cref="RingBufferValue{T}"/>.</param>
        /// <param name="pulse">The Heart Beat Interval. Also reused as the grace period bounding a single
        /// pooled item's <c>Dispose()</c>/<c>DisposeAsync()</c> call during shutdown or scale-down, whether
        /// or not <c>HeartBeat</c> itself is configured. Default value is 10 seconds.</param>
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
        /// Enables autoscale (scale up) in reaction to real-time acquire demand, and permanently removes manual switching
        /// from the built service's type (see <see cref="IRingBufferManualScaleService{T}"/>) — the two are mutually exclusive.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Since v6.0.0 (ADR001V03), the scale-up process is triggered by the backlog-reactive signal: it
        /// reacts to real-time waiting-caller depth (how many concurrent <c>AcquireAsync</c> callers are
        /// genuinely waiting for an idle item right now), not to a count of past acquire faults/timeouts.
        /// <paramref name="numberOfFaults"/> is currently unused - it is kept on this method's signature only
        /// pending the public-surface redesign (ADR007V03); it has no effect on when or how a scale-up
        /// triggers today.
        /// </para>
        /// A scale-up (or scale-down) that only partially completes can land the buffer strictly between two
        /// named capacities - scale-down evaluation still applies from there, not only from the exact initial
        /// or maximum capacity. There is no scale-down from minimum capacity: minimum capacity is the floor.
        /// <para>
        /// While above initial capacity (including, but not limited to, exactly maximum capacity), the
        /// scale-down target is initial capacity, evaluated when the median <b>exceeds</b> the formula:
        /// current capacity - initial capacity + 2, capped so the threshold never reaches current capacity
        /// itself (see below). At exactly maximum capacity, and whenever this cap does not apply, this is the
        /// same as: maximum capacity - initial capacity + 2.
        /// </para>
        /// <para>
        /// While at or below initial capacity (including, but not limited to, exactly initial capacity),
        /// down to minimum capacity, the scale-down target is minimum capacity, evaluated when the median
        /// <b>reaches or exceeds</b> the formula: current capacity - minimum capacity + 2, with the same cap.
        /// At exactly initial capacity, and whenever the cap does not apply, this is the same as: initial
        /// capacity - minimum capacity + 2.
        /// </para>
        /// <para>
        /// Both thresholds are capped at current capacity - 1: if initial capacity is 2 (the minimum legal
        /// value), the upper-band formula would otherwise reduce to current capacity itself, a value the
        /// median can never exceed - making scale-down from above initial capacity unreachable regardless of
        /// position, including at maximum capacity. Likewise, if minimum capacity is 2, the lower-band
        /// formula would otherwise require the median to equal current capacity exactly, i.e. zero
        /// acquisitions across the entire sampling window, to ever scale down to the floor. The cap keeps
        /// both bands reachable in every configuration without changing behavior anywhere the uncapped
        /// formula was already below it.
        /// </para>
        /// <para>
        /// The scale-down process is executed against the median of the samples collected via
        /// <see cref="IRingBufferBuilder{T}.ElasticCapacity(int, int, int, int?, TimeSpan?, int?)"/>, per the two
        /// formulas above.
        /// </para>
        /// <para>
        /// Scale-up has a deadline based on the factory's own per-item timeout (see
        /// <see cref="IRingBufferBuilder{T}.Factory(Func{CancellationToken, Task{T}}, TimeSpan?, byte)"/>); scale-down never
        /// waits at all. Neither direction "undoes" a partial result - a scale-up that only creates some of the
        /// needed items keeps them, and a scale-down that only finds some items idle removes just those.
        /// </para>
        /// <para>
        /// The backlog-reactive signal carries no counter to forget: it is re-evaluated fresh every time a
        /// caller starts waiting and every time a scale-up batch completes, from the buffer's actual
        /// real-time state (waiting callers vs. idle items) rather than from accumulated history - so there
        /// is nothing that needs resetting between an earlier scale-up and a later one.
        /// </para>
        /// </remarks>
        /// <param name="numberOfFaults">Unused since v6.0.0 (ADR001V03) - see the remarks above. Kept on the
        /// signature pending the public-surface redesign (ADR007V03). Default is 1.</param>
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
