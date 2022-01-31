
namespace RingBufferPlus.Features
{
    internal struct AutoScalerCount
    {
        private readonly object _sync = new();

        public AutoScalerCount()
        {
            _errorCount = 0;
            _waitCount = 0;
            _acquisitionCount = 0;
            _timeoutCount = 0;
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
            _syncCount = false;
        }

    }
}
