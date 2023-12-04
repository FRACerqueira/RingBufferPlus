// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    internal class RingBufferManager<T> : IRingBufferService<T>, IRingBufferWarmup<T>, IRingBufferSwith, IRingBufferCallback, IDisposable
    {
        private readonly BlockingCollection<RingBufferValue<T>> _blockrenewBuffer = [];
        private readonly BlockingCollection<RingBufferException> _blockexceptionsBuffer = [];
        private readonly BlockingCollection<DateTime> _blockRetryFactoryBuffer = [];
        private readonly BlockingCollection<RingBufferMetric> _blockreportBuffer = [];
        private readonly BlockingCollection<int> _blockScaleBuffer = [];
        private readonly List<int> _MetricBuffer = [];

        private Task _renewBufferThread;
        private Task _retryFactoryThread;
        private Task _errorBufferThread;
        private Task _metricBufferThread;
        private Task _scaleCapacityThread;

        private Task _bufferHealthThread;

        private Task _reportscaleCapacityThread;

        private IRingBufferSwith _swithFrom;
        private readonly IRingBufferSwith _slaveBuffer;

        private readonly Func<CancellationToken, T> _factoryHandler;
        private readonly Func<T, bool> _healthHandler;
        private readonly Action<ILogger?, RingBufferException> _errorHandler;
        private readonly Action<RingBufferMetric, ILogger?, CancellationToken?> _reportHandler;

        private readonly object _lockMetric = new();
        private readonly object _lockWarmup = new();
        private readonly object _lockhealth = new();


        private readonly CancellationToken _apptoken;
        private readonly CancellationTokenSource _managertoken;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _WarmupComplete;
        private bool _WarmupRunning;

        private int _currentCapacity;

        private readonly object _lockAccquire = new();
        private readonly ConcurrentQueue<T> _availableBuffer = new();
        private int _counterAccquire;
        private int _counterBuffer;
        private bool _recoveryBuffer;
        private bool _runningScale;
        private DateTime _lastacquisition;

        public RingBufferManager(IRingBufferOptions<T> ringBufferOptions, CancellationToken cancellationToken)
        {
            _apptoken = cancellationToken;
            _apptoken.Register(() => Dispose(true));
            _lastacquisition = DateTime.Now;

            Name = ringBufferOptions.Name;
            Capacity = ringBufferOptions.Capacity;
            MinCapacity = ringBufferOptions.MinCapacity;
            MaxCapacity = ringBufferOptions.MaxCapacity;
            FactoryTimeout = ringBufferOptions.FactoryTimeout;
            FactoryIdleRetry = ringBufferOptions.FactoryIdleRetryError;
            ScaleCapacity =
                (ringBufferOptions.ScaleToMaxLessEq.HasValue && ringBufferOptions.MaxRollbackWhenFreeGreaterEq.HasValue) ||
                (ringBufferOptions.ScaleToMinGreaterEq.HasValue && ringBufferOptions.MinRollbackWhenFreeLessEq.HasValue);
            SampleUnit = ringBufferOptions.ScaleCapacityDelay / ringBufferOptions.SampleUnit;
            SamplesCount = ringBufferOptions.SampleUnit;
            ScaleToMin = ringBufferOptions.ScaleToMinGreaterEq;
            RollbackFromMin = ringBufferOptions.MinRollbackWhenFreeLessEq;
            TriggerFromMin = ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq;
            ScaleToMax = ringBufferOptions.ScaleToMaxLessEq;
            RollbackFromMax = ringBufferOptions.MaxRollbackWhenFreeGreaterEq;
            TriggerFromMax = ringBufferOptions.MaxTriggerByAccqWhenFreeGreaterEq;
            AccquireTimeout = ringBufferOptions.AccquireTimeout;
            BufferHealtTimeout = ringBufferOptions.BufferHealtTimeout;
            UserScale = ringBufferOptions.UserSwithScale;

            _logger = ringBufferOptions.Logger;
            _factoryHandler = ringBufferOptions.FactoryHandler;
            _healthHandler = ringBufferOptions.BufferHealthHandler;
            _errorHandler = ringBufferOptions.ErrorHandler;
            _reportHandler = ringBufferOptions.ReportHandler;
            _slaveBuffer = ringBufferOptions.SwithTo;
            _swithFrom = null;
            IsSlave = ringBufferOptions.IsSlave;
            if (!IsSlave && _slaveBuffer is not null)
            {
                ((IRingBufferCallback)_slaveBuffer).CallBackMaster(this);
            }

            _currentCapacity = 0;
            _managertoken = CancellationTokenSource.CreateLinkedTokenSource(_apptoken);
        }

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _managertoken?.Cancel();
                _reportscaleCapacityThread?.Wait(TimeSpan.FromSeconds(10));
                _scaleCapacityThread?.Wait(TimeSpan.FromSeconds(10));
                _metricBufferThread?.Wait(TimeSpan.FromSeconds(10));
                _retryFactoryThread?.Wait(TimeSpan.FromSeconds(10));
                _renewBufferThread?.Wait(TimeSpan.FromSeconds(10));
                _bufferHealthThread?.Wait(TimeSpan.FromSeconds(10));

                _blockrenewBuffer?.Dispose();
                _blockexceptionsBuffer?.Dispose();
                _blockRetryFactoryBuffer?.Dispose();
                _blockScaleBuffer?.Dispose();
                _blockreportBuffer?.Dispose();
                _bufferHealthThread?.Dispose();

                if (_availableBuffer is not null)
                {
                    T[] buffer = [.. _availableBuffer];
                    _availableBuffer.Clear();
                    foreach (T item in buffer)
                    {
                        if (item is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                }
                SemaphoremasterSlave.Dispose();
                _managertoken?.Dispose();
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region IRingBufferService properies

        public string Name { get; }

        public int Capacity { get; }

        public int MinCapacity { get; }

        public int MaxCapacity { get; }

        public TimeSpan FactoryTimeout { get; }

        public TimeSpan BufferHealtTimeout { get; }

        public TimeSpan FactoryIdleRetry { get; }

        public bool ScaleCapacity { get; }

        public TimeSpan SampleUnit { get; }

        public int SamplesCount { get; }

        public int? ScaleToMin { get; }

        public int? RollbackFromMin { get; }

        public int? TriggerFromMin { get; }

        public int? ScaleToMax { get; }

        public int? RollbackFromMax { get; }

        public int? TriggerFromMax { get; }

        public TimeSpan AccquireTimeout { get; }

        public ScaleMode UserScale { get;}

        #endregion

        #region IRingBufferService 

        public RingBufferValue<T> Accquire(CancellationToken? cancellation = null)
        {
            CancellationToken localcancellation = cancellation ?? CancellationToken.None;
            T result = default;

            //if not Warmup? execute
            if (!_WarmupComplete)
            {
                //Warmup
                if (Warmup())
                {
                    WriteLogDebug(DateTime.Now, $"{Name} Wait Warmup Completed");
                }
                else
                {
                    WriteLogDebug(DateTime.Now, $"{Name} Accquire Send Warmup Timeout to OnError Handler");
                    //Send error
                    _blockexceptionsBuffer.Add(new RingBufferException(Name, "Accquire Warmup Timeout"), _managertoken.Token);
                }

            }

            if (localcancellation.IsCancellationRequested)
            {
                return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
            }

            if (_recoveryBuffer)
            {
                int fullavailable;
                lock (_lockAccquire)
                {
                    fullavailable = _counterAccquire + _counterBuffer;
                }
                if (fullavailable != _currentCapacity)
                {
                    localcancellation.WaitHandle.WaitOne(5);
                    return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
                }
                lock (_lockAccquire)
                {
                    _recoveryBuffer = false;
                }
                WriteLogWarning(DateTime.Now, $"{Name} Removed State RecoveryBuffer");
            }

            if (_healthHandler is not null)
            {
                lock (_lockhealth)
                {
                    _lastacquisition = DateTime.Now;
                }
            }

            //try Accquire buffer && 
            while (!localcancellation.IsCancellationRequested)
            {

                var sw = Stopwatch.StartNew();
                var exist = _availableBuffer.TryDequeue(out result);
                if (exist)
                {
                    var hc = _healthHandler?.Invoke(result);
                    if (hc.HasValue && !hc.Value)
                    {
                        if (result is IDisposable disposablevalue)
                        {
                            disposablevalue.Dispose();
                            WriteLogDebug(DateTime.Now, $"{Name} Accquire Disposed Item");
                        }
                        _blockRetryFactoryBuffer.Add(DateTime.Now);    
                        exist = false;
                    }
                    if (exist)
                    {
                        int available;
                        lock (_lockAccquire)
                        {
                            _counterAccquire++;
                            _counterBuffer--;
                            //when the scale down sync occurs, the counters are reset
                            if (_counterBuffer < 0)
                            {
                                _counterBuffer = 0;
                            }
                            available = _counterBuffer;
                        }
                        //trigger to default capacity
                        if (!localcancellation.IsCancellationRequested &&
                            ScaleCapacity &&
                            ((TriggerFromMin.HasValue && _currentCapacity == MinCapacity && available <= TriggerFromMin) ||
                                (TriggerFromMax.HasValue && _currentCapacity == MaxCapacity && available >= TriggerFromMax)))
                        {
                            _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleType.ToDefaultCapacity));
                            WriteLogDebug(DateTime.Now, $"{Name} Accquire Invoked {ScaleType.ToDefaultCapacity} : {Capacity}");
                        }
                        sw.Stop();
                        //ok
                        return new RingBufferValue<T>(Name, sw.Elapsed, true, result, DisposeBuffer);
                    }
                }
                else
                {
                    int fullavailable;
                    lock (_lockAccquire)
                    {
                        fullavailable = _counterAccquire + _counterBuffer;
                    }
                    if (sw.Elapsed > AccquireTimeout)
                    {
                        sw.Stop();
                        if (!_recoveryBuffer && _currentCapacity != 0 && fullavailable == 0)
                        {
                            lock (_lockAccquire)
                            {
                                _recoveryBuffer = true;
                            }
                            WriteLogWarning(DateTime.Now, $"{Name} with State RecoveryBuffer");
                            _blockexceptionsBuffer.Add(new RingBufferException(Name, $"{Name} with State RecoveryBuffer"));
                            var mode = ScaleType.ToDefaultCapacity;
                            if (ScaleCapacity && _currentCapacity == MaxCapacity)
                            {
                                mode = ScaleType.ToMaxCapacity;
                            }
                            else if (ScaleCapacity && _currentCapacity == MinCapacity)
                            {
                                mode = ScaleType.ToMinCapacity;
                            }
                            _blockrenewBuffer.Add(new RingBufferValue<T>(mode));
                        }
                        WriteLogDebug(DateTime.Now, $"{Name} Accquire timeout {sw.Elapsed}");
                        //Send error
                        _blockexceptionsBuffer.Add(new RingBufferException(Name, $"Accquire timeout {sw.Elapsed}"), _managertoken.Token);
                        break;
                    }
                    localcancellation.WaitHandle.WaitOne(5);
                }
            }
            //not ok
            return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
        }

        public void SwithTo(ScaleSwith value)
        {
            if (UserScale == ScaleMode.Automatic)
            {
                throw new InvalidOperationException($"RingBuffer is configured to autoscale!");

            }
            ScaleType newscale;
            switch (value)
            {
                case ScaleSwith.ToMinCapacity:
                    if (_currentCapacity == MinCapacity)
                    {
                        return;
                    }
                    newscale = ScaleType.ToMinCapacity;
                    break;
                case ScaleSwith.ToMaxCapacity:
                    if (_currentCapacity == MaxCapacity)
                    {
                        return;
                    }
                    newscale = ScaleType.ToMaxCapacity;
                    break;
                case ScaleSwith.ToDefaultCapacity:
                    if (_currentCapacity == Capacity)
                    {
                        return;
                    }
                    newscale = ScaleType.ToDefaultCapacity;
                    break;
                default:
                    throw new NotImplementedException($"ScaleUnit {value}");
            }
            _blockrenewBuffer.Add(new RingBufferValue<T>(newscale));
        }

        #endregion

        #region IRingBufferWarmup

        public bool Warmup(TimeSpan? timeout = null)
        {
            var tm = timeout ?? TimeSpan.FromSeconds(30); ;
            Startup(tm);
            lock (_lockAccquire)
            {
                return _counterBuffer + _counterAccquire == Capacity;
            }

        }

        #endregion

        #region IRingBufferSwith

        public bool SyncSwithTo(ScaleType scaleMode)
        {
            if (!_WarmupComplete)
            {
                return false;
            }
            if (_swithFrom is null)
            {
                throw new InvalidOperationException($"{Name}: Not found Ring Buffer from Swith");
            }

            var newcap = scaleMode switch
            {
                ScaleType.ReNew => throw new InvalidOperationException($"{Name}: {scaleMode} Not valid to Swith"),
                ScaleType.ToMinCapacity => MinCapacity,
                ScaleType.ToMaxCapacity => MaxCapacity,
                ScaleType.ToDefaultCapacity => Capacity,
                _ => throw new ArgumentException($"scaleMode Not found {scaleMode}"),
            };
            if (_currentCapacity != newcap)
            {
                if (_reportHandler != null)
                {
                    WriteLogDebug(DateTime.Now, $"{Name} SwithTo Invoked {SourceTrigger.MasterSlave} : {scaleMode} and Send Metric To Report Thread");
                    _blockreportBuffer.Add(new RingBufferMetric(SourceTrigger.MasterSlave, _currentCapacity, newcap));
                }
                var diff = newcap - _currentCapacity;
                if (diff < 0)
                {
                    lock (_lockAccquire)
                    {
                        lock (_lockMetric)
                        {
                            _runningScale = true;
                        }
                        //nenew all buffer and resert counts
                        RemoveAllBuffer(null);
                        TryLoadBufferAsync(newcap);
                    }
                }
                else
                {
                    lock (_lockAccquire)
                    {
                        diff = newcap - (_counterBuffer + _counterAccquire);
                    }
                    if (diff > 0)
                    {
                        lock (_lockMetric)
                        {
                            _runningScale = true;
                        }
                        TryLoadBufferAsync(diff);
                    }
                }
                lock (_lockMetric)
                {
                    //clear metric
                    _MetricBuffer.Clear();
                    _currentCapacity = newcap;
                    _runningScale = false;
                }
                if (((IRingBufferCallback)_swithFrom).SemaphoremasterSlave.CurrentCount == 0)
                {
                    //notify master
                    ((IRingBufferCallback)_swithFrom).SemaphoremasterSlave.Release();
                    WriteLogTrace(DateTime.Now, $"{Name} Slave send to Master SemaphoremasterSlave Release");
                }
                return true;
            }
            return false;

        }

        #endregion

        #region IRingBufferCallback


        public void CallBackMaster(IRingBufferSwith value)
        {
            _swithFrom = value;
        }

        public bool IsSlave { get; private set; }

        public SemaphoreSlim SemaphoremasterSlave { get; } = new(1, 1);

        #endregion

        private void Startup(TimeSpan timeoutfullcapacity)
        {
            if (_WarmupComplete)
            {
                return;
            }
            else
            {
                lock (_lockWarmup)
                {
                    if (_WarmupRunning)
                    {
                        return;
                    }
                    _WarmupRunning = true;
                }
            }


            _bufferHealthThread = new Task(() =>
            {
                if (_healthHandler != null)
                {
                    while (!_managertoken.IsCancellationRequested)
                    {
                        DateTime oldacquisition;
                        DateTime lastacquisition;
                        lock (_lockhealth)
                        {
                            lastacquisition = _lastacquisition;
                            oldacquisition = lastacquisition.Add(BufferHealtTimeout);
                        }
                        if (oldacquisition < DateTime.Now)
                        {
                            // Check all buffer (Idle acquisition)
                            int qtd = _availableBuffer.Count;
                            WriteLogInfo(DateTime.Now, $"{Name} Internal Buffer Health init with ({qtd})");
                            while (_availableBuffer.TryDequeue(out var value))
                            {
                                 if (!_healthHandler.Invoke(value))
                                {
                                    if (value is IDisposable disposablevalue)
                                    {
                                        disposablevalue.Dispose();
                                    }
                                    _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleType.ReNew));
                                }
                                else
                                {
                                    _availableBuffer.Enqueue(value);
                                }
                                //not Idle acquisition
                                if (oldacquisition != _lastacquisition)
                                {
                                    break;
                                }
                            }
                            lock (_lockhealth)
                            {
                                if (lastacquisition == _lastacquisition)
                                {
                                    _lastacquisition = DateTime.Now.Add(BufferHealtTimeout);
                                }
                            }
                            WriteLogInfo(DateTime.Now, $"{Name} Internal Buffer Health done");
                        }
                        _managertoken.Token.WaitHandle.WaitOne(100);
                    }
                    WriteLogInfo(DateTime.Now, $"{Name} Buffer Health Thread Stoped");
                }
                else
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Buffer Health Thread Stoped");
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Buffer Health Thread Created");
            _bufferHealthThread.Start();

            _renewBufferThread = new Task(() =>
            {
                try
                {
                    foreach (var item in _blockrenewBuffer.GetConsumingEnumerable(_managertoken.Token))
                    {
                        if (item.SkipTurnback || item.IsScaleCapacity)
                        {
                            if (item.SkipTurnback)
                            {
                                if (item.Current is not null && item.Current is IDisposable disposable)
                                {
                                    disposable.Dispose();
                                }
                                lock (_lockAccquire)
                                {
                                    _counterBuffer--;
                                    //when the scale down sync occurs, the counters are reset
                                    if (_counterBuffer < 0)
                                    {
                                        _counterBuffer = 0;
                                    }
                                    _counterAccquire--;
                                    //when the scale down sync occurs, the counters are reset
                                    if (_counterAccquire < 0)
                                    {
                                        _counterAccquire = 0;
                                    }
                                }
                                //create a new item
                                TryLoadBufferAsync(1);
                            }
                            else if (item.IsScaleCapacity)
                            {
                                var newcap = _currentCapacity;
                                switch (item.ScaleMode)
                                {
                                    case ScaleType.ToDefaultCapacity:
                                        newcap = Capacity;
                                        break;
                                    case ScaleType.ToMinCapacity:
                                        newcap = MinCapacity;
                                        break;
                                    case ScaleType.ToMaxCapacity:
                                        newcap = MaxCapacity;
                                        break;
                                }

                                WriteLogTrace(DateTime.Now, $"{Name} Master SemaphoremasterSlave Block");
                                SemaphoremasterSlave.Wait();
                                WriteLogTrace(DateTime.Now, $"{Name} Master SemaphoremasterSlave Block done");

                                if (_slaveBuffer is not null && item.ScaleMode != ScaleType.ReNew)
                                {
                                    var slavename = ((IRingBufferCallback)_slaveBuffer).Name;
                                    WriteLogDebug(DateTime.Now, $"{Name} Master invoke  SwithTo to Slave({slavename}) with scale: {item.ScaleMode}");
                                    if (_slaveBuffer.SyncSwithTo(item.ScaleMode))
                                    {
                                        WriteLogTrace(DateTime.Now, $"{Name} Master wait SemaphoremasterSlave Release from {slavename}");
                                        SemaphoremasterSlave.Wait();
                                        WriteLogTrace(DateTime.Now, $"{Name} Master done SemaphoremasterSlave Release from {slavename}");
                                    }
                                    else
                                    {
                                        WriteLogTrace(DateTime.Now, $"{Name} Slave({slavename}) already has {item.ScaleMode}");
                                    }
                                }
                                var diff = newcap - _currentCapacity;
                                if ((item.ScaleMode != ScaleType.ReNew && diff < 0))
                                {
                                    lock (_lockAccquire)
                                    {
                                        lock (_lockMetric)
                                        {
                                            _runningScale = true;
                                        }
                                        //nenew all buffer and resert counts
                                        if (_slaveBuffer is not null)
                                        {
                                            RemoveAllBuffer(null);
                                        }
                                        else
                                        {
                                            RemoveAllBuffer(diff*-1);
                                        }
                                        TryLoadBufferAsync(newcap);
                                    }
                                }
                                else
                                {
                                    lock (_lockAccquire)
                                    {
                                        diff = newcap - (_counterBuffer + _counterAccquire);
                                    }
                                    if (diff > 0)
                                    {
                                        lock (_lockMetric)
                                        {
                                            _runningScale = true;
                                        }
                                        TryLoadBufferAsync(diff);
                                    }
                                }
                                lock (_MetricBuffer)
                                {
                                    _MetricBuffer.Clear();
                                    _currentCapacity = newcap;
                                    _runningScale = false;
                                }

                                if (SemaphoremasterSlave.CurrentCount == 0)
                                {
                                    SemaphoremasterSlave.Release();
                                    WriteLogTrace(DateTime.Now, $"{Name} Masater SemaphoremasterSlave Block done, already has {item.ScaleMode} in slave");
                                }
                            }
                        }
                        else if (item.Successful)
                        {
                            lock (_lockAccquire)
                            {
                                _availableBuffer.Enqueue(item.Current);
                                _counterBuffer++;
                                _counterAccquire--;
                                //when the scale down sync occurs, the counters are reset
                                if (_counterAccquire < 0)
                                {
                                    _counterAccquire = 0;
                                }
                            }
                            WriteLogDebug(DateTime.Now, $"{Name} Renew Rehydrated Buffer");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Renew Buffer Thread Stoped");
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Renew Buffer Thread Created");
            _renewBufferThread.Start();

            _errorBufferThread = new Task(() =>
            {
                if (_errorHandler != null)
                {
                    try
                    {
                        foreach (var item in _blockexceptionsBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            try
                            {
                                WriteLogDebug(DateTime.Now, $"{Name} OnError Handler Invoked");
                                _errorHandler?.Invoke(_logger, item);
                            }
                            catch (Exception ex)
                            {
                                var dtref = DateTime.Now;
                                _logger?.Log(LogLevel.Error, "[{dtref}] Error again! OnError Handler : {ex}", dtref, ex);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //none
                    }
                    finally
                    {
                        WriteLogInfo(DateTime.Now, $"{Name} Log Error Buffer Thread Stoped");
                    }
                }
                else
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Log Error Buffer Thread Stoped");
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Log Error Buffer Thread Created");
            _errorBufferThread.Start();

            _retryFactoryThread = new Task(() =>
            {
                try
                {
                    foreach (var item in _blockRetryFactoryBuffer.GetConsumingEnumerable(_managertoken.Token))
                    {
                        var diff = TimeSpan.Zero;
                        if (item > DateTime.Now)
                        {
                            diff = item - DateTime.Now;
                        }
                        WriteLogDebug(DateTime.Now, $"{Name} Wait Retry Factory {diff}");
                        _managertoken.Token.WaitHandle.WaitOne(diff);
                        if (!_managertoken.IsCancellationRequested)
                        {
                            WriteLogDebug(DateTime.Now, $"{Name} Retry Factory Send Message Create to Renew Buffer Thread");
                            _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleType.ReNew));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Retry Factory Thread Stoped");
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Retry Factory Thread Created");
            _retryFactoryThread.Start();

            _metricBufferThread = new Task(() =>
            {
                if (ScaleCapacity && UserScale == ScaleMode.Automatic)
                {
                    while (!_managertoken.IsCancellationRequested && !_WarmupComplete)
                    {
                        _managertoken.Token.WaitHandle.WaitOne(5);
                    }
                }
                while (ScaleCapacity && UserScale == ScaleMode.Automatic  && !_managertoken.IsCancellationRequested)
                {
                    _managertoken.Token.WaitHandle.WaitOne(SampleUnit);

                    if (_currentCapacity == 0 || _recoveryBuffer || _runningScale)
                    {
                        continue;
                    }
                    if (_managertoken.IsCancellationRequested)
                    {
                        continue;
                    }
                    lock (_lockMetric)
                    {
                        var available = _counterBuffer;
                        _MetricBuffer.Add(available);
                        WriteLogDebug(DateTime.Now, $"{Name} Metric Added Available information: {available}, {_MetricBuffer.Count}/{SamplesCount}");
                        if (_MetricBuffer.Count >= SamplesCount)
                        {
                            var tmp = _MetricBuffer.OrderBy(x => x).ToArray();
                            double median = 0;
                            if (tmp.Length % 2 == 0)
                            {
                                var pos = tmp.Length / 2;
                                median = (tmp[pos - 1] + tmp[pos]) / 2.0;
                            }
                            else
                            {
                                var pos = (tmp.Length + 1) / 2;
                                median = tmp[pos - 1];
                            }
                            _MetricBuffer.Clear();
                            if (!_managertoken.IsCancellationRequested)
                            {
                                _blockScaleBuffer.Add(Convert.ToInt32(Math.Ceiling(median)));
                                WriteLogTrace(DateTime.Now, $"{Name} Metric Resume Created({median} / {Convert.ToInt32(Math.Ceiling(median))})");
                            }
                        }
                    }
                }
                WriteLogInfo(DateTime.Now, $"{Name} Metric Buffer Thread Stoped");
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Metric Buffer Thread Created");
            _metricBufferThread.Start();

            _scaleCapacityThread = new Task(() =>
            {
                if (ScaleCapacity && UserScale == ScaleMode.Automatic)
                {
                    try
                    {
                        foreach (var freebuffer in _blockScaleBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            if (_recoveryBuffer)
                            {
                                continue;
                            }
                            var newcap = _currentCapacity;
                            var currentcap = newcap;
                            ScaleType? mode = null;
                            if (ScaleToMax.HasValue && freebuffer <= ScaleToMax.Value && currentcap == Capacity)
                            {
                                newcap = MaxCapacity;
                                mode = ScaleType.ToMaxCapacity;
                            }
                            else if (ScaleToMin.HasValue && freebuffer >= ScaleToMin.Value && currentcap == Capacity)
                            {
                                newcap = MinCapacity;
                                mode = ScaleType.ToMinCapacity;
                            }
                            else if (RollbackFromMin.HasValue && freebuffer <= RollbackFromMin.Value && currentcap == MinCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleType.ToDefaultCapacity;
                            }
                            else if (RollbackFromMax.HasValue && freebuffer >= RollbackFromMax.Value && currentcap == MaxCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleType.ToDefaultCapacity;
                            }
                            if (mode.HasValue && !_managertoken.IsCancellationRequested)
                            {
                                if (_reportHandler != null)
                                {
                                    WriteLogDebug(DateTime.Now, $"{Name} Scale Capacity Invoked {mode} : {newcap} and Send Metric To Report Thread");
                                    _blockreportBuffer.Add(new RingBufferMetric(SourceTrigger.AutoScale, currentcap, newcap));
                                }
                                WriteLogDebug(DateTime.Now, $"{Name} Scale Capacity Send Message Create to Renew Buffer Thread");
                                _blockrenewBuffer.Add(new RingBufferValue<T>(mode.Value));
                                lock (_lockMetric)
                                {
                                    _MetricBuffer.Clear();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //none
                    }
                }
                WriteLogInfo(DateTime.Now, $"{Name} Scale Capacity Thread Stoped");
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Scale Capacity Thread Created");
            _scaleCapacityThread.Start();

            _reportscaleCapacityThread = new Task(() =>
            {
                if (_reportHandler != null)
                {
                    try
                    {
                        foreach (var item in _blockreportBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            try
                            {
                                WriteLogDebug(DateTime.Now, $"{Name} ReportHandler Invoked");
                                _reportHandler?.Invoke(item, _logger, _managertoken.Token);
                            }
                            catch (Exception ex)
                            {
                                var dtref = DateTime.Now;
                                _logger?.Log(LogLevel.Error, "[{dtref}] Error ReportScale Handler : {ex}", dtref, ex);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //none
                    }
                    finally
                    {
                        WriteLogInfo(DateTime.Now, $"{Name} Report Scale Capacity Thread Stoped");
                    }
                }
                else
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Report Scale Capacity Thread Stoped");
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, $"{Name} Report Scale Capacity Thread Created");
            _reportscaleCapacityThread.Start();

            using var warmupcts = CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);

            WriteLogInfo(DateTime.Now, $"{Name} Creating {Capacity} items");

            _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleType.ToDefaultCapacity));

            warmupcts.CancelAfter(timeoutfullcapacity);
            while (!warmupcts.Token.IsCancellationRequested && _counterBuffer != Capacity)
            {
                warmupcts.Token.WaitHandle.WaitOne(5);
            }

            WriteLogInfo(DateTime.Now, $"{Name} Warmup complete with {_counterBuffer} items of {Capacity}");
            if (_counterBuffer != Capacity)
            {
                _recoveryBuffer = true;
                WriteLogWarning(DateTime.Now, $"{Name} with State RecoveryBuffer");
                _blockexceptionsBuffer.Add(new RingBufferException(Name, $"{Name} with State RecoveryBuffer"));
            }

            _currentCapacity = Capacity;

            lock (_lockWarmup)
            {
                _WarmupComplete = true;
                _WarmupRunning = false;
            }
        }

        private void DisposeBuffer(RingBufferValue<T> value)
        {
            if (!_managertoken.IsCancellationRequested)
            {
                _blockrenewBuffer.Add(value);
            }
        }

        private void RemoveAllBuffer(int? maxcount)
        {
            WriteLogDebug(DateTime.Now, $"{Name} Remove All Buffer");
            var localmax = int.MaxValue;
            if (maxcount.HasValue)
            {
                localmax = maxcount.Value;
            }
            var count = 0;
            while (_availableBuffer.TryDequeue(out var value))
            {
                if (value is IDisposable disposablevalue)
                {
                    disposablevalue.Dispose();
                }
                count++;
                if (count >= localmax)
                {
                    break;
                }
            }
            if (!maxcount.HasValue)
            {
                _counterBuffer = 0;
            }
            else
            {
                _counterBuffer -= count;
                if (_counterBuffer < 0)
                {
                    _counterBuffer = 0;
                }
            }
            _counterAccquire = 0;
        }

        private void TryLoadBufferAsync(int diff)
        {
            var qtderr = 0;
            //33% error stop! 
            var maxerr =  (int)Math.Ceiling(diff / 3.0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < diff; i++)
            {
                using var ctstimeout = CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);
                ctstimeout.CancelAfter(FactoryTimeout);
                try
                {
                    var tk = Task.Run(() =>
                    {
                        //33% error stop! 
                        if (qtderr > maxerr)
                        {
                            //force error
                            throw new RingBufferException(Name, "TryLoadBufferAsync Factory Exception");
                        }
                        if (!ctstimeout.IsCancellationRequested)
                        {
                            var value = _factoryHandler.Invoke(ctstimeout.Token);
                            if (ctstimeout.IsCancellationRequested)
                            {
                                if (value is not null && value is IDisposable disposablevalue)
                                {
                                    disposablevalue.Dispose();
                                }
                            }
                            else
                            {
                                if (value is null)
                                {
                                    WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry");
                                    _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                                }
                                else
                                {
                                    _availableBuffer.Enqueue(value);
                                    _counterBuffer++;
                                    WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Added New Item To Buffer : {_availableBuffer.Count} , Available : {_counterBuffer} Unavailable : {_counterAccquire}");
                                }
                            }
                        }
                    });
                    tk.Wait(ctstimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    qtderr++;
                    //Send to retry
                    WriteLogWarning(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry (Timeout)");
                    _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                }
                catch (Exception ex)
                {
                    qtderr++;
                    //Send error
                    _blockexceptionsBuffer.Add(new RingBufferException(Name, "TryLoadBufferAsync Factory Exception", ex));
                    //Send to retry
                    _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                }

            }
            sw.Stop();
            if (qtderr > maxerr)
            {
                WriteLogWarning(DateTime.Now, $"{Name} TryLoadBufferAsync Factory many Exception, Max is {maxerr} of ({qtderr}/{diff})");
            }
            if (sw.Elapsed < FactoryTimeout && qtderr != 0)
            {
                //force wait timeout;
                _managertoken.Token.WaitHandle.WaitOne(FactoryTimeout.Subtract(sw.Elapsed));
            }
        }

        private void WriteLogDebug(DateTime dtref, string message)
        {
            _logger?.Log(LogLevel.Debug, "[{dtref}] {message}", dtref, message);
        }

        private void WriteLogInfo(DateTime dtref, string message)
        {
            _logger?.Log(LogLevel.Information, "[{dtref}] {message}", dtref, message);
        }

        private void WriteLogTrace(DateTime dtref, string message)
        {
            _logger?.Log(LogLevel.Trace, "[{dtref}] {message}", dtref, message);
        }

        private void WriteLogWarning(DateTime dtref, string message)
        {
            _logger?.Log(LogLevel.Warning, "[{dtref}] {message}", dtref, message);
        }


    }
}
