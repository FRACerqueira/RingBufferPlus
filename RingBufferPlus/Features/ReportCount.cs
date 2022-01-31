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
            _syncCount = false;
        }

        private volatile int _timeoutCount;
        public int TimeoutCount
        {
            get
            {
                if (_syncCount)
                {
                    lock (_sync)
                    {
                        return _timeoutCount;
                    }
                }
                return _timeoutCount;
            }
        }
        public void IncrementTimeout()
        {
            lock (_sync)
            {
                _timeoutCount++;
            }
        }

        private volatile int _acquisitionCount;
        public int AcquisitionCount
        {
            get
            {
                if (_syncCount)
                {
                    lock (_sync)
                    {
                        return _acquisitionCount;
                    }
                }
                return _acquisitionCount;
            }
        }

        public void IncrementAcquisitionSucceeded()
        {
            lock (_sync)
            {
                _acquisitionSucceededCount++;
            }
        }


        private volatile int _acquisitionSucceededCount;
        public int AcquisitionSucceeded
        {
            get
            {
                if (_syncCount)
                {
                    lock (_sync)
                    {
                        return _acquisitionSucceededCount;
                    }
                }
                return _acquisitionSucceededCount;
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
                    int acq = _acquisitionSucceededCount;
                    double aux = 0;
                    if (acq > 0)
                    {
                        aux = _totalSucceededexec.TotalMilliseconds / acq;
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

        private volatile int _waitCount;
        public int WaitCount
        {
            get
            {
                if (_syncCount)
                {
                    lock (_sync)
                    {
                        return _waitCount;
                    }
                }
                return _waitCount;
            }
        }
        public void IncrementWaitCount()
        {
            lock (_sync)
            {
                _waitCount++;
            }
        }

        private volatile int _errorCount;
        public int ErrorCount
        {
            get
            {
                if (_syncCount)
                {
                    lock (_sync)
                    {
                        return _errorCount;
                    }
                }
                return _errorCount;
            }
        }
        public void IncrementErrorCount()
        {
            lock (_sync)
            {
                _errorCount++;
            }
        }

        private volatile bool _syncCount;
        public bool SyncCount
        {
            get => _syncCount;
            set => _syncCount = value;
        }

        public void ResetCount()
        {
            _syncCount = true;
            _errorCount = 0;
            _waitCount = 0;
            _acquisitionCount = 0;
            _timeoutCount = 0;
            _totalSucceededexec = TimeSpan.Zero;
            _acquisitionSucceededCount = 0;
            _syncCount = false;
        }
    }
}
