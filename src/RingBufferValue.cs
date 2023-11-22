// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents acquired the value in the buffer.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public class RingBufferValue<T> : IDisposable
    {
        private readonly Action<RingBufferValue<T>>? _turnback;
        private readonly string _name;
        private bool _disposed;


        private RingBufferValue()
        {
        }

        /// <summary>
        /// Create empty RingBufferValue.
        /// </summary>
        public RingBufferValue(int diffCapacity)
        {
            IsScaleCapacity = true;
            Current = default;
            DiffCapacity = diffCapacity;
        }

        /// <summary>
        /// Create RingBufferValue.
        /// </summary>
        /// <param name="name">Name of RingBuffer.</param>
        /// <param name="elapsedTime">Elapsed time to acquire the value.</param>
        /// <param name="succeeded">Successful Acquire.</param>
        /// <param name="value">The buffer value.</param>
        /// <param name="turnback">The action handler to turn back buffer when disposed.</param>
        public RingBufferValue(string name, TimeSpan elapsedTime, bool succeeded, T value,Action<RingBufferValue<T>>? turnback) 
        {
            DiffCapacity = 0;
            ElapsedTime = elapsedTime;
            Successful = succeeded;
            Current = value;
            _turnback = turnback;
            _name = name;
        }

        /// <summary>
        /// Name of RingBuffer.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Elapsed time to acquire the value.
        /// </summary>
        public TimeSpan ElapsedTime { get; }

        /// <summary>
        /// Successful Acquire.
        /// </summary>
        public bool Successful { get; }

        /// <summary>
        /// The buffer value.
        /// </summary>
        public T Current { get; }


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
        /// <param name="disposing">Disposing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _turnback?.Invoke(this);
            }
        }


        /// <summary>
        /// Turnback value to buffer.
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        internal bool SkipTurnback { get; set; }
        internal bool IsScaleCapacity { get; set; }
        internal int DiffCapacity { get; }

    }
}
