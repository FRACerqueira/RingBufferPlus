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
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

        /// <summary>
        /// Sets the HeartBeat in the ring buffer: periodically inspects a live item's health.
        /// </summary>
        /// <remarks>
        /// At each <paramref name="pulse"/>, an item is acquired from the buffer and handed to
        /// <paramref name="value"/> for inspection - the framework owns acquiring and returning it,
        /// not your callback (ADR007V03): there is no disposable object handed to you to misuse.
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
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> HeartBeat(Func<T, bool> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> AcquireTimeout(TimeSpan value);

        /// <summary>
        /// Sets the error handler, invoked inline whenever this buffer logs an error internally.
        /// </summary>
        /// <param name="errorHandler">The handler to invoke with the error. Called synchronously,
        /// inline (ADR007V03) - no background queue. The logger configured via <see cref="Logger(ILogger?)"/>
        /// is a separate concern; this handler does not receive it.</param>
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> OnError(Action<Exception> errorHandler);

        /// <summary>
        /// Sets a fixed capacity for the ring buffer: no autoscale, no manual switch, no min/max range.
        /// </summary>
        /// <param name="value">The fixed capacity. Value must be greater than or equal to 2.</param>
        /// <returns>An instance of <see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> FixedCapacity(int value);

        /// <summary>
        /// Sets an elastic capacity for the ring buffer, enabling manual switching between
        /// <paramref name="minCapacity"/>, <paramref name="target"/> and <paramref name="maxCapacity"/>
        /// via <see cref="IRingBufferManualScaleService{T}.SwitchToAsync(ScaleSwitch, TimeSpan)"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Since v6.0.0 (ADR007V03), <paramref name="minCapacity"/>/<paramref name="maxCapacity"/> are the
        /// first two parameters and <paramref name="target"/> - the startup capacity, what
        /// <see cref="ScaleSwitch.InitCapacity"/> returns to - is optional, defaulting to
        /// <paramref name="minCapacity"/> ("provision for demand, not worst case": start small and let the
        /// floor guard/backlog-reactive signal/Monitor grow it, rather than defaulting to some arbitrary
        /// middle value). <paramref name="minCapacity"/> equal to <paramref name="maxCapacity"/> means no real
        /// elasticity at all - a valid, degenerate configuration, not an error - and in that case
        /// <paramref name="target"/>, if given explicitly, must equal both, else <c>Build</c>/
        /// <c>BuildWarmupAsync</c> throws the same way an out-of-range <paramref name="target"/> already does.
        /// </para>
        /// <paramref name="baseTimer"/>/<paramref name="numberSamples"/> configure the Monitor's sampling
        /// cadence and sliding-window size (ADR003V03) - the Monitor is always active for an elastic pool
        /// (ADR001V03/ADR007V03) and can dispatch either a scale-up or a scale-down (see
        /// <see cref="IRingBufferElasticBuilder{T}.MonitorTuning(double, double, double, int)"/> for the
        /// rest of that algorithm's parameters). They do not bound a scale-up or scale-down operation's own
        /// deadline. A scale-up's deadline is <c>quantity * FactoryTimeout</c> (see
        /// <see cref="IRingBufferBuilder{T}.Factory(Func{CancellationToken, Task{T}}, TimeSpan?, byte)"/>); a
        /// scale-down never waits at all. Neither direction undoes a partial result on timeout - whatever
        /// capacity was actually gained or removed is kept.
        /// </remarks>
        /// <param name="minCapacity">The minimal buffer capacity. Value must be greater than or equal to 2.</param>
        /// <param name="maxCapacity">The maximum buffer capacity. Value must be greater than or equal to <paramref name="minCapacity"/>.</param>
        /// <param name="target">Initial/startup capacity - the target to provision for. Value must be greater
        /// than or equal to <paramref name="minCapacity"/> and less than or equal to <paramref name="maxCapacity"/>.
        /// Defaults to <paramref name="minCapacity"/> when not given.</param>
        /// <param name="numberSamples">Number of samples in the Monitor's sliding window. Default is 100 (one
        /// sample per 300ms). The window is only cleared when an actual scale operation fires (gated by
        /// <see cref="IRingBufferElasticBuilder{T}.MonitorTuning(double, double, double, int)"/>'s
        /// <c>deadband</c>) - during a genuinely steady period (demand stable, every computed target landing
        /// inside the deadband), nothing clears it, so it fills to <paramref name="numberSamples"/> and slides.
        /// The window's real-time span is always exactly <paramref name="baseTimer"/> regardless of this
        /// parameter's value (the per-sample interval is <paramref name="baseTimer"/> divided by this
        /// parameter, so the two cancel out) - see <paramref name="baseTimer"/> for the adaptation-horizon
        /// trade-off this parameter does NOT control. Raising or lowering this value alone only changes how
        /// many discrete points make up that same time span - fewer points means a coarser, noisier
        /// percentile/trend estimate; more points means a smoother one at the cost of one <see
        /// cref="System.Threading.Channels.Channel{T}"/> write (a Tick command) per sample.</param>
        /// <param name="baseTimer">The <see cref="TimeSpan"/> interval to collect samples - and, since the
        /// Monitor's sliding window (<paramref name="numberSamples"/>) always spans exactly this much real
        /// time regardless of how many samples it holds, this IS the adaptation horizon: at the default (30
        /// seconds), a demand drop that arrives during a genuinely steady period (see
        /// <paramref name="numberSamples"/>) can take roughly this long before the Monitor's own slow layer
        /// reacts to it - the buffer's higher-priority signals (floor guard, backlog-reactive) already cover
        /// the more urgent cases (an actual waiting caller, a floor breach) far faster than this, regardless
        /// of this value. Shortening this value is the only way to shrink that horizon; doing so makes every
        /// Tick fire more often too unless <paramref name="numberSamples"/> is lowered proportionally to keep
        /// the per-sample interval unchanged. Default value is 30 seconds (one sample per 300ms).</param>
        /// <param name="maxConcurrentFactoryCalls">Maximum number of concurrent factory calls when
        /// creating several items at once (the initial warmup fill, or a scale-up). Bounds a large
        /// batch from flooding a struggling-but-technically-accepting downstream with simultaneous
        /// creation attempts (e.g. database/broker connections) - see ADR001V03. Default is 4.</param>
        /// <returns>An instance of <see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> ElasticCapacity(int minCapacity, int maxCapacity, int? target = null, int? numberSamples = null, TimeSpan? baseTimer = null, int? maxConcurrentFactoryCalls = null);
    }
}
