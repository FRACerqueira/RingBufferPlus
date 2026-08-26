// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents acquired the value in the buffer.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    /// <remarks>
    /// Create RingBufferValue.
    /// </remarks>
    /// <param name="name">Name of RingBuffer.</param>
    /// <param name="elapsedTime">Elapsed time to acquire the value.</param>
    /// <param name="succeeded">Successful Acquire.</param>
    /// <param name="value">The buffer value.</param>
    /// <param name="turnback">The async handler to turn back the buffer when disposed.</param>
    public sealed class RingBufferValue<T>(string name, TimeSpan elapsedTime, bool succeeded, T value, Func<RingBufferValue<T>, ValueTask>? turnback) : IAsyncDisposable
    {
        private readonly Func<RingBufferValue<T>, ValueTask>? _turnback = turnback;
        private readonly string _name = name;
        private int _disposed;

        /// <summary>
        /// Name of RingBuffer.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Elapsed time to acquire the value.
        /// </summary>
        public TimeSpan ElapsedTime { get; } = elapsedTime;

        /// <summary>
        /// Successful Acquire.
        /// </summary>
        public bool Successful { get; } = succeeded;

        /// <summary>
        /// The buffer value.
        /// </summary>
        public T Current { get; init; } = value;

        /// <summary>
        /// Invalidates the return of the value to the buffer. A replacement instance will be created.
        /// <para>This command will be ignored if the acquire was unsuccessful.</para>
        /// </summary>
        public void Invalidate()
        {
            if (Successful)
            {
                SkipTurnback = true;
            }
        }

        /// <summary>
        /// Turns back the value to the buffer asynchronously.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            if (_turnback is not null)
            {
                await _turnback(this).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Indicates whether to skip turning back the value to the buffer.
        /// </summary>
        internal bool SkipTurnback { get; set; }
    }
}
