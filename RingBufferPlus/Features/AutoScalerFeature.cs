using RingBufferPlus.ObjectValues;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus.Features
{
    internal class AutoScalerFeature
    {
        private readonly CancellationToken _token;
        private readonly object _sync = new();
        private readonly Func<RingBufferMetric, CancellationToken, Task<int>>? _itemAsync;
        private readonly Func<RingBufferMetric, CancellationToken, int>? _itemSync;
        private readonly int _maxAvaliable;
        private readonly int _minAvaliable;
        public TimeSpan BaseTime { get; }

        private AutoScalerFeature()
        {
        }

        public AutoScalerFeature(
            int maxAvaliable,
            int minAvaliable,
            TimeSpan baseTime,
            Func<RingBufferMetric, CancellationToken, Task<int>>? itemAsnc,
            Func<RingBufferMetric, CancellationToken, int>? itemsync,
            CancellationToken cancellationToken) : this()
        {
            _maxAvaliable = maxAvaliable;
            _minAvaliable = minAvaliable;
            BaseTime = baseTime;
            _itemAsync = itemAsnc;
            _itemSync = itemsync;
            _token = cancellationToken;
        }

        public bool ExistFuncAsync => _itemAsync != null;
        public bool ExistFuncSync => _itemSync != null;

        internal bool ExistFunc => ExistFuncAsync || ExistFuncSync;

        public int MaxAvaliable => _maxAvaliable;
        public int MinAvaliable => _minAvaliable;

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

        public async Task<int> ExecuteAync(RingBufferMetric metric)
        {
            ResetCount();

            if (ExistFuncAsync)
            {
                return await _itemAsync(metric, _token).ConfigureAwait(false);
            }
            return _itemSync(metric, _token);
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
