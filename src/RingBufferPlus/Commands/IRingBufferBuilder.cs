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
        /// <returns><see cref="IRingBufferBuilder{T}"/>.</returns>
        IRingBufferBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

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
        /// <paramref name="minCapacity"/> and <paramref name="maxCapacity"/> are required.
        /// <paramref name="target"/> - the startup capacity, and what <see cref="ScaleSwitch.InitCapacity"/>
        /// returns to - is optional and defaults to <paramref name="minCapacity"/>: start small and let the
        /// floor guard, backlog-reactive signal, and Monitor grow it, rather than picking an arbitrary
        /// middle value. Setting <paramref name="minCapacity"/> equal to <paramref name="maxCapacity"/> is
        /// valid - it just means no real elasticity. In that case, an explicit <paramref name="target"/>
        /// must equal both, or <c>Build</c>/<c>BuildWarmupAsync</c> throws, the same as any other
        /// out-of-range <paramref name="target"/>.
        /// </para>
        /// <paramref name="baseTimer"/> and <paramref name="numberSamples"/> configure the Monitor's
        /// sampling cadence and sliding-window size (see
        /// <see cref="IRingBufferElasticBuilder{T}.MonitorTuning(double, double, double, int)"/> for its
        /// other parameters). The Monitor is always active for an elastic pool and can trigger either a
        /// scale-up or a scale-down.
        /// Neither parameter bounds how long a scale operation itself can take. A scale-up's deadline is
        /// <c>quantity * FactoryTimeout</c> (see
        /// <see cref="IRingBufferBuilder{T}.Factory(Func{CancellationToken, Task{T}}, TimeSpan?, byte)"/>); a
        /// scale-down never waits. Either way, a timeout never undoes a partial result - whatever capacity
        /// was actually gained or removed is kept.
        /// </remarks>
        /// <param name="minCapacity">The minimal buffer capacity. Value must be greater than or equal to 2.</param>
        /// <param name="maxCapacity">The maximum buffer capacity. Value must be greater than or equal to <paramref name="minCapacity"/>.</param>
        /// <param name="target">Initial/startup capacity - the target to provision for. Value must be greater
        /// than or equal to <paramref name="minCapacity"/> and less than or equal to <paramref name="maxCapacity"/>.
        /// Defaults to <paramref name="minCapacity"/> when not given.</param>
        /// <param name="numberSamples">Number of samples in the Monitor's sliding window. Default is 100
        /// (one sample per 300ms).
        /// The window's real-time span is always exactly <paramref name="baseTimer"/>, no matter what this
        /// value is - the per-sample interval is <paramref name="baseTimer"/> divided by this number, so the
        /// two cancel out. Raising or lowering it only changes how many points make up that same time span:
        /// fewer points give a coarser, noisier estimate; more points give a smoother one, at the cost of
        /// one extra <see cref="System.Threading.Channels.Channel{T}"/> write (a Tick command) per sample.
        /// See <paramref name="baseTimer"/> for the adaptation-horizon trade-off this parameter does NOT
        /// control.
        /// The window is only cleared when a scale operation actually fires (gated by
        /// <see cref="IRingBufferElasticBuilder{T}.MonitorTuning(double, double, double, int)"/>'s
        /// <c>deadband</c>). During a steady period it is never cleared - it just fills up and
        /// slides.</param>
        /// <param name="baseTimer">The <see cref="TimeSpan"/> interval over which samples are collected.
        /// Default is 30 seconds (one sample per 300ms).
        /// This is the Monitor's adaptation horizon: since its sliding window always spans exactly this much
        /// real time, a demand drop that arrives during a steady period can take roughly this long to be
        /// reacted to. The buffer's higher-priority signals (floor guard, backlog-reactive) already cover
        /// the urgent cases - an actual waiting caller, a floor breach - far faster than this, regardless of
        /// this value.
        /// Shortening this value is the only way to shrink that horizon. Doing so also makes every Tick fire
        /// more often, unless you lower <paramref name="numberSamples"/> proportionally to keep the
        /// per-sample interval the same.</param>
        /// <param name="maxConcurrentFactoryCalls">Maximum number of concurrent factory calls when
        /// creating several items at once (the initial warmup fill, or a scale-up). Bounds a large batch
        /// from flooding a downstream that's struggling but still accepting connections (e.g. a database
        /// or broker) with simultaneous creation attempts. Default is 4.</param>
        /// <returns>An instance of <see cref="IRingBufferElasticBuilder{T}"/>.</returns>
        IRingBufferElasticBuilder<T> ElasticCapacity(int minCapacity, int maxCapacity, int? target = null, int? numberSamples = null, TimeSpan? baseTimer = null, int? maxConcurrentFactoryCalls = null);
    }
}
