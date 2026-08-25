// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents a RingBufferPlus builder committed to a fixed capacity.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferFixedBuilder<T>
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
        /// Factory calls also run with bounded concurrency (default
        /// <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/> - fixed-capacity buffers can't
        /// configure this) rather than one at a time. Giving up only stops items still waiting for a slot -
        /// calls already in flight keep running. So with the default, a fully broken factory can still make
        /// up to <see cref="RingBufferDefault.MaxConcurrentFactoryCalls"/> concurrent attempts, not just
        /// one.</param>
        /// <returns><see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null, byte maxConsecutiveFactoryFailures = 0);

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
        /// <returns><see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> HeartBeat(Func<T, bool> value, TimeSpan? pulse = null);

        /// <summary>
        /// Sets the logger.
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> Logger(ILogger? value);

        /// <summary>
        /// Sets the timeout to acquire buffer.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> AcquireTimeout(TimeSpan value);

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
        /// <returns><see cref="IRingBufferFixedBuilder{T}"/>.</returns>
        IRingBufferFixedBuilder<T> OnError(Action<Exception> errorHandler);

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
        /// <remarks>
        /// It is recommended to use this method in the initialization of the application.
        /// </remarks>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an instance of <see cref="IRingBufferService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach its capacity.</exception>
        Task<IRingBufferService<T>> BuildWarmupAsync(CancellationToken cancellation = default);
    }
}
