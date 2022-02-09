using System;
using System.Threading;

namespace RingBufferPlus.Features
{
    internal class RingBufferCount
    {
        private readonly object _sync = new();

        public RingBufferCount()
        {
            _errorCount = 0;
            _waitCount = 0;
            _acquisitionCount = 0;
            _timeoutCount = 0;
            _totalSucceededexec = TimeSpan.Zero;
            _acquisitionSucceededCount = 0;
            _averageSucceededExecution = TimeSpan.Zero;
        }

        private volatile int _timeoutCount;
        public int TimeoutCount => _timeoutCount;
        public void IncrementTimeout()
        {
            Interlocked.Increment(ref _timeoutCount);
        }

        private volatile int _acquisitionSucceededCount;
        public int AcquisitionSucceeded => _acquisitionSucceededCount;

        private TimeSpan _totalSucceededexec;
        private TimeSpan _averageSucceededExecution;
        public TimeSpan AverageSucceeded => _averageSucceededExecution;
        public void IncrementAcquisitionSucceeded(TimeSpan value)
        {
            var newok = Interlocked.Increment(ref _acquisitionSucceededCount);
            lock (_sync)
            {
                _totalSucceededexec = _totalSucceededexec.Add(value);
                int acq = newok;
                double aux = 0;
                if (acq > 0)
                {
                    aux = _totalSucceededexec.TotalMilliseconds / acq;
                }
                _averageSucceededExecution = TimeSpan.FromMilliseconds(aux);
            }
        }

        private volatile int _acquisitionCount;
        public int AcquisitionCount => _acquisitionCount;
        public void IncrementAcquisition()
        {
            Interlocked.Increment(ref _acquisitionCount);
        }

        private volatile int _waitCount;
        public int WaitCount => _waitCount;
        public void IncrementWaitCount()
        {
            Interlocked.Increment(ref _waitCount);
        }

        private volatile int _errorCount;
        public int ErrorCount => _errorCount;
        public void IncrementErrorCount()
        {
            Interlocked.Increment(ref _errorCount);
        }

        public void ResetCount()
        {
            lock (_sync)
            {
                Interlocked.Exchange(ref _errorCount, 0);
                Interlocked.Exchange(ref _waitCount, 0);
                Interlocked.Exchange(ref _acquisitionCount, 0);
                Interlocked.Exchange(ref _timeoutCount, 0);
                Interlocked.Exchange(ref _acquisitionSucceededCount, 0);
                _totalSucceededexec = TimeSpan.Zero;
                _averageSucceededExecution = TimeSpan.Zero;
            }
        }
    }
}
