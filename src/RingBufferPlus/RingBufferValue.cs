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
    /// <param name="turnback">The action handler to turn back buffer when disposed.</param>
    public sealed class RingBufferValue<T>(string name, TimeSpan elapsedTime, bool succeeded, T value, Action<RingBufferValue<T>>? turnback) : IDisposable
    {
        private readonly Action<RingBufferValue<T>>? _turnback = turnback;
        private readonly string _name = name;
        private bool _disposed;

        /// <summary>
        /// Name of RingBuffer.
        /// </summary>
        public string? Name => _name;

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
        /// Invalidates the return of the value to the buffer. Another instance will be created.
        /// <br>This command will be ignored if the return was unsuccessful.</br>
        /// </summary>
        public void Invalidate()
        {
            if (Successful)
            {
                SkipTurnback = true;
            }
        }

        /// <summary>
        /// Turnback value to buffer.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _turnback?.Invoke(this);
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Indicates whether to skip turning back the value to the buffer.
        /// </summary>
        internal bool SkipTurnback { get; set; }

    }
}
