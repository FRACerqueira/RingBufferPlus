using System;

namespace RingBufferPlus.ObjectValues
{
    public class RingBufferValue<T> : IDisposable
    {
        private readonly Action<T,bool>? _turnback;
        private bool disposedValue;

        private RingBufferValue()
        {
        }

        internal RingBufferValue(string alias, int available,int running, long elapsedTime, bool succeeded, Exception error, T value, Action<T,bool>? turnback) : this()
        {
            Available = available;
            Running = running;
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
        public int Running { get; }
        public int Capacity => Available + Running;
        internal bool SkiptTurnback { get; set; }

        public void Invalidate()
        {
            SkiptTurnback = true;
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (SucceededAccquire)
                    {
                        _turnback?.Invoke(Current,SkiptTurnback);
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
