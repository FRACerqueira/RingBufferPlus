using System;

namespace RingBufferPlus.ObjectValues
{
    public class RingBufferValue<T> : IDisposable
    {
        private readonly Action<T>? _turnback;
        private bool disposedValue;

        private RingBufferValue()
        {
        }

        internal RingBufferValue(string alias, int available, long elapsedTime, bool succeeded, Exception error, T value, Action<T>? turnback) : this()
        {
            Available = available;
            ElapsedTime = elapsedTime;
            Alias = alias;
            SucceededAccquire = succeeded;
            Error = error;
            Current = value;
            _turnback = turnback;
        }

        public long ElapsedTime { get; }
        public string Alias { get; }
        public bool SucceededAccquire { get; }
        public T Current { get; }
        public Exception Error { get; }
        public int Available { get; }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (SucceededAccquire)
                    {
                        _turnback?.Invoke(Current);
                    }
                    disposedValue = true;
                }
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

    }
}
