using System;

namespace RingBufferPlus.ObjectValues
{
    public class RingBufferValue<T> : IDisposable
    {
        private readonly Action<RingBufferValue<T>>? _turnback;
        private bool disposedValue;
        private NaturalTimer _timer;
        private RingBufferValue()
        {
        }

        internal RingBufferValue(string alias, RingBufferfState state, long elapsedTime, bool succeeded, Exception error, T value, Action<RingBufferValue<T>>? turnback) : this()
        {
            ElapsedAccquire = elapsedTime;
            Alias = alias;
            SucceededAccquire = succeeded;
            Error = error;
            Current = value;
            HasSick = state.FailureState;
            _turnback = turnback;
            _timer = new NaturalTimer();
            _timer.Start();
        }
        public RingBufferfState State { get; }
        public bool HasSick { get; }
        public long ElapsedAccquire { get; }
        public string Alias { get; }
        public bool SucceededAccquire { get; }
        public T Current { get; }
        public Exception Error { get; }

        internal bool SkiptTurnback { get; set; }
        internal TimeSpan ElapsedExecute
        {
            get
            {
                _timer.Stop();
                return _timer.Elapsed;
            }
        }

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
                    _turnback?.Invoke(this);
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
