using System;

namespace RingBufferPlus.Features
{
    internal struct ReportCount
    {
        private readonly object _sync = new();

        public ReportCount()
        {
            _errorCount = 0;
            _waitCount = 0;
            _acquisitionCount = 0;
            _timeoutCount = 0;
            _totalSucceededexec = TimeSpan.Zero;
            _acquisitionSucceededCount = 0;
        }

        private long _timeoutCount;
        public long TimeoutCount
        {
            get
            {
                lock (_sync)
                {
                    return _timeoutCount;
                }
            }
        }
        public void IncrementTimeout()
        {
            lock (_sync)
            {
                _timeoutCount++;
            }
        }
        public void DecrementTimeout()
        {
            lock (_sync)
            {
                _timeoutCount--;
            }
        }

        private long _acquisitionCount;
        public long AcquisitionCount
        {
            get
            {
                lock (_sync)
                {
                    return _acquisitionCount;
                }
            }
        }

        public void IncrementAcquisitionSucceeded()
        {
            lock (_sync)
            {
                _acquisitionSucceededCount++;
            }
        }


        private long _acquisitionSucceededCount;
        public long AcquisitionSucceeded
        {
            get
            {
                lock (_sync)
                {
                    return _acquisitionSucceededCount;
                }
            }
        }

        private TimeSpan _totalSucceededexec;

        public void AddTotaSucceededlExecution(TimeSpan value)
        {
            lock (_sync)
            {
                _totalSucceededexec = _totalSucceededexec.Add(value);
            }
        }

        public TimeSpan AverageSucceededExecution
        {
            get
            {
                lock (_sync)
                {
                    double aux = 0;
                    if (_acquisitionSucceededCount > 0)
                    {
                        aux = _totalSucceededexec.TotalMilliseconds / _acquisitionSucceededCount;
                    }
                    return TimeSpan.FromMilliseconds(aux);
                }
            }
        }



        public void IncrementAcquisition()
        {
            lock (_sync)
            {
                _acquisitionCount++;
            }
        }
        public void DecrementAcquisition()
        {
            lock (_sync)
            {
                _acquisitionCount--;
            }
        }

        private long _waitCount;
        public long WaitCount
        {
            get
            {
                lock (_sync)
                {
                    return _waitCount;
                }
            }
        }
        public void IncrementWaitCount()
        {
            lock (_sync)
            {
                _waitCount++;
            }
        }
        public void DecrementWaitCount()
        {
            lock (_sync)
            {
                _waitCount--;
            }
        }

        private long _errorCount;
        public long ErrorCount
        {
            get
            {
                lock (_sync)
                {
                    return _errorCount;
                }
            }
        }
        public void IncrementErrorCount()
        {
            lock (_sync)
            {
                _errorCount++;
            }
        }
        public void DecrementErrorCount()
        {
            lock (_sync)
            {
                _errorCount--;
            }
        }

        public void ResetCount()
        {
            lock (_sync)
            {
                _errorCount = 0;
                _waitCount = 0;
                _acquisitionCount = 0;
                _timeoutCount = 0;
                _totalSucceededexec = TimeSpan.Zero;
                _acquisitionSucceededCount = 0;
            }
        }
    }
}
