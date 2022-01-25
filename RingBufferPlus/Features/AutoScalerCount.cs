
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
            }
        }

    }
}
