// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
        private readonly BlockingCollection<(ScaleMode, RingBufferMetric)> _blockreportBuffer = [];
        private readonly BlockingCollection<int> _blockScaleBuffer = [];
        private readonly List<int> _MetricBuffer = [];

        private Task _renewBufferThread;
        private Task _retryFactoryThread;
        private Task _logErrorBufferThread;
        private Task _metricBufferThread;
        private Task _scaleCapacityThread;
        private Task _reportscaleCapacityThread;

        private IRingBufferSwith _swithFrom;
        private readonly IRingBufferSwith _slaveBuffer;

        private readonly Func<CancellationToken, T> _factory;
        private readonly Action<ILogger?, RingBufferException> _errorHandler;
        private readonly Action<ScaleMode, ILogger, RingBufferMetric, CancellationToken> _reportHandler;

        private readonly object _lockMetric = new();
        private readonly object _lockWarmup = new();


        private readonly CancellationToken _apptoken;
        private readonly CancellationTokenSource _managertoken;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _WarmupComplete;
        private bool _WarmupRunning;

        private int _currentCapacity;

        private readonly object _lockAccquire = new();
        private readonly Queue<T> _availableBuffer = new();
        private int _counterAccquire;
        private int _counterBuffer;
        private bool _recoveryBuffer;

        public RingBufferManager(IRingBufferOptions<T> ringBufferOptions, CancellationToken cancellationToken)
        {
            _apptoken = cancellationToken;
            _apptoken.Register(() => Dispose(true));

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

            _logger = ringBufferOptions.Logger;
            _factory = ringBufferOptions.FactoryHandler;
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

                _blockrenewBuffer?.Dispose();
                _blockexceptionsBuffer?.Dispose();
                _blockRetryFactoryBuffer?.Dispose();
                _blockScaleBuffer?.Dispose();
                _blockreportBuffer?.Dispose();

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

        #endregion

        #region IRingBufferService 

        public RingBufferValue<T> Accquire(CancellationToken? cancellation = null)
        {
            CancellationToken localcancellation;
            T result;
            bool ok;


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

            lock (_lockAccquire)
            {
                localcancellation = cancellation ?? CancellationToken.None;
                result = default;
                ok = false;
            }

            if (localcancellation.IsCancellationRequested)
            {
                return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
            }

            var sw = Stopwatch.StartNew();

            if (_recoveryBuffer)
            {
                if (_counterAccquire + _counterBuffer != _currentCapacity)
                {
                    localcancellation.WaitHandle.WaitOne(5);
                    return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
                }
                _recoveryBuffer = false;
                WriteLogInfo(DateTime.Now, $"{Name} Removed State RecoveryBuffer");
            }

            //try Accquire buffer
            int available = 0;
            while (!localcancellation.IsCancellationRequested && !ok)
            {
                var exist = false;
                lock (_lockAccquire)
                {
                    exist = _availableBuffer.TryDequeue(out result);
                }
                if (exist)
                {
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
                    ok = true;
                }
                else
                {
                    if (sw.Elapsed > AccquireTimeout)
                    {
                        break;
                    }
                    localcancellation.WaitHandle.WaitOne(5);
                }
            }
            sw.Stop();
            //Accquire timeout
            if (!ok)
            {
                if (!_recoveryBuffer && _currentCapacity != 0)
                {
                    if (_counterAccquire + _counterBuffer == 0)
                    {
                        _recoveryBuffer = true;
                        WriteLogDebug(DateTime.Now, $"{Name} with State RecoveryBuffer");
                        _blockexceptionsBuffer.Add(new RingBufferException(Name, $"{Name} with State RecoveryBuffer"));
                        var mode = ScaleMode.ToDefaultCapacity;
                        if (ScaleCapacity && _currentCapacity == MaxCapacity)
                        {
                            mode = ScaleMode.ToMaxCapacity;
                        }
                        else if (ScaleCapacity && _currentCapacity == MinCapacity)
                        {
                            mode = ScaleMode.ToMinCapacity;
                        }
                        _blockrenewBuffer.Add(new RingBufferValue<T>(mode));
                    }
                }
                WriteLogDebug(DateTime.Now, $"{Name} Accquire timeout {sw.Elapsed}");
                //Send error
                _blockexceptionsBuffer.Add(new RingBufferException(Name, $"Accquire timeout {sw.Elapsed}, Current Capacity : {_currentCapacity} Available: {_counterAccquire} Unavailable: {_counterAccquire}"), _managertoken.Token);
                return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
            }

            //trigger to default capacity
            if (!localcancellation.IsCancellationRequested &&
                ScaleCapacity &&
                ((TriggerFromMin.HasValue && _currentCapacity == MinCapacity && available <= TriggerFromMin) ||
                 (TriggerFromMax.HasValue && _currentCapacity == MaxCapacity && available >= TriggerFromMax)))
            {
                lock (_lockMetric)
                {
                    _MetricBuffer.Clear();
                }
                _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleMode.ToDefaultCapacity));
                WriteLogDebug(DateTime.Now, $"{Name} Accquire Invoked {ScaleMode.ToDefaultCapacity} : {Capacity}");
            }
            //ok
            return new RingBufferValue<T>(Name, sw.Elapsed, true, result, DisposeBuffer);
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

        public bool SwithTo(ScaleMode scaleMode)
        {
            if (!_WarmupComplete)
            {
                return false;
            }
            if (_swithFrom is null)
            {
                throw new InvalidOperationException($"{Name}: Not found Ring Buffer from Swith");
            }
            lock (_lockAccquire)
            {
                var newcap = scaleMode switch
                {
                    ScaleMode.None => throw new InvalidOperationException($"{Name}: {scaleMode} Not valid to Swith"),
                    ScaleMode.ToMinCapacity => MinCapacity,
                    ScaleMode.ToMaxCapacity => MaxCapacity,
                    ScaleMode.ToDefaultCapacity => Capacity,
                    _ => throw new ArgumentException($"scaleMode Not found {scaleMode}"),
                };

                if (_currentCapacity != newcap)
                {
                    if (_reportHandler != null)
                    {
                        WriteLogDebug(DateTime.Now, $"{Name} SwithTo Invoked {SourceTrigger.MasterSlave} : {scaleMode} and Send Metric To Report Thread");
                        _blockreportBuffer.Add((scaleMode, new RingBufferMetric(SourceTrigger.MasterSlave, _currentCapacity, newcap)));
                    }
                    var diff = newcap - _currentCapacity;
                    if (diff < 0)
                    {
                        //nenew all buffer and resert counts
                        RemoveAllBuffer();
                        TryLoadBufferAsync(newcap);
                    }
                    else
                    {
                        diff = newcap - (_counterBuffer + _counterAccquire);
                        if (diff > 0)
                        {
                            TryLoadBufferAsync(diff);
                        }
                    }
                    lock (_lockMetric)
                    {
                        //clear metric
                        _MetricBuffer.Clear();
                    }
                    _currentCapacity = newcap;
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

            _renewBufferThread = new Task(() =>
            {
                try
                {
                    foreach (var item in _blockrenewBuffer.GetConsumingEnumerable(_managertoken.Token))
                    {
                        if (item.Successful || item.IsScaleCapacity)
                        {
                            if (item.SkipTurnback || item.IsScaleCapacity)
                            {
                                if (item.SkipTurnback)
                                {
                                    var disposabledone = false;
                                    lock (_lockAccquire)
                                    {
                                        if (item.Current is not null && item.Current is IDisposable disposable)
                                        {
                                            disposable.Dispose();
                                            disposabledone = true;
                                        }
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
                                    if (disposabledone)
                                    {
                                        WriteLogDebug(DateTime.Now, $"{Name} Renew Buffer Disposed Item");
                                    }
                                    _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleMode.None));
                                }
                                else if (item.IsScaleCapacity)
                                {
                                    var newcap = _currentCapacity;
                                    switch (item.ScaleMode)
                                    {
                                        case ScaleMode.ToDefaultCapacity:
                                            newcap = Capacity;
                                            break;
                                        case ScaleMode.ToMinCapacity:
                                            newcap = MinCapacity;
                                            break;
                                        case ScaleMode.ToMaxCapacity:
                                            newcap = MaxCapacity;
                                            break;
                                    }

                                    WriteLogTrace(DateTime.Now, $"{Name} Master SemaphoremasterSlave Block");
                                    SemaphoremasterSlave.Wait();
                                    WriteLogTrace(DateTime.Now, $"{Name} Master SemaphoremasterSlave Block done");

                                    if (_slaveBuffer is not null && item.ScaleMode != ScaleMode.None)
                                    {
                                        var slavename = ((IRingBufferCallback)_slaveBuffer).Name;
                                        WriteLogDebug(DateTime.Now, $"{Name} Master invoke  SwithTo to Slave({slavename}) with scale: {item.ScaleMode}");
                                        if (_slaveBuffer.SwithTo(item.ScaleMode))
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
                                    lock (_lockAccquire)
                                    {
                                        var diff = newcap - _currentCapacity;
                                        if ((item.ScaleMode != ScaleMode.None && diff < 0))
                                        {
                                            //nenew all buffer and resert counts
                                            RemoveAllBuffer();
                                            TryLoadBufferAsync(newcap);
                                        }
                                        else
                                        {
                                            diff = newcap - (_counterBuffer + _counterAccquire);
                                            if (diff > 0)
                                            {
                                                TryLoadBufferAsync(diff);
                                            }
                                        }
                                        _currentCapacity = newcap;
                                    }
                                    if (SemaphoremasterSlave.CurrentCount == 0)
                                    {
                                        SemaphoremasterSlave.Release();
                                        WriteLogTrace(DateTime.Now, $"{Name} Masater SemaphoremasterSlave Block done, already has {item.ScaleMode} in slave");
                                    }
                                }
                            }
                            else
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
                                    WriteLogDebug(DateTime.Now, $"{Name} Renew Rehydrated Buffer");
                                }
                            }
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

            _logErrorBufferThread = new Task(() =>
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
            _logErrorBufferThread.Start();

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
                        if (!_managertoken.Token.IsCancellationRequested)
                        {
                            WriteLogInfo(DateTime.Now, $"{Name} Retry Factory Send Message Create to Renew Buffer Thread");
                            _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleMode.None));
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
                while (!_managertoken.Token.IsCancellationRequested && !_WarmupComplete)
                {
                    _managertoken.Token.WaitHandle.WaitOne(5);
                }
                while (ScaleCapacity && !_managertoken.Token.IsCancellationRequested)
                {
                    _managertoken.Token.WaitHandle.WaitOne(SampleUnit);

                    if (_currentCapacity == 0 || _recoveryBuffer)
                    {
                        if (_MetricBuffer.Count > 0)
                        {
                            lock (_lockMetric)
                            {
                                _MetricBuffer.Clear();
                            }
                        }
                        continue;
                    }
                    if (_managertoken.Token.IsCancellationRequested)
                    {
                        continue;
                    }
                    lock (_lockMetric)
                    {
                        WriteLogDebug(DateTime.Now, $"{Name} Metric Available: {_counterBuffer} Unavailable: {_counterAccquire}");
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
                            if (!_managertoken.Token.IsCancellationRequested)
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
                if (ScaleCapacity)
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
                            ScaleMode? mode = null;
                            if (ScaleToMax.HasValue && freebuffer <= ScaleToMax.Value && currentcap == Capacity)
                            {
                                newcap = MaxCapacity;
                                mode = ScaleMode.ToMaxCapacity;
                            }
                            else if (ScaleToMin.HasValue && freebuffer >= ScaleToMin.Value && currentcap == Capacity)
                            {
                                newcap = MinCapacity;
                                mode = ScaleMode.ToMinCapacity;
                            }
                            else if (RollbackFromMin.HasValue && freebuffer <= RollbackFromMin.Value && currentcap == MinCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                            }
                            else if (RollbackFromMax.HasValue && freebuffer >= RollbackFromMax.Value && currentcap == MaxCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                            }
                            if (mode.HasValue && !_managertoken.Token.IsCancellationRequested)
                            {
                                if (_reportHandler != null)
                                {
                                    WriteLogDebug(DateTime.Now, $"{Name} Scale Capacity Invoked {mode} : {newcap} and Send Metric To Report Thread");
                                    _blockreportBuffer.Add((mode.Value, new RingBufferMetric(SourceTrigger.AutoScale, currentcap, newcap)));
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
                        foreach ((ScaleMode Mode, RingBufferMetric Metric) in _blockreportBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            try
                            {
                                WriteLogDebug(DateTime.Now, $"{Name} ReportHandler Invoked");
                                _reportHandler?.Invoke(Mode, _logger, Metric, _managertoken.Token);
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

            _blockrenewBuffer.Add(new RingBufferValue<T>(ScaleMode.ToDefaultCapacity));

            warmupcts.CancelAfter(timeoutfullcapacity);
            while (!warmupcts.Token.IsCancellationRequested && _counterBuffer != Capacity)
            {
                warmupcts.Token.WaitHandle.WaitOne(5);
            }

            WriteLogInfo(DateTime.Now, $"{Name} Warmup complete with {_counterBuffer} items of {Capacity}");
            if (_counterBuffer != Capacity)
            {
                _recoveryBuffer = true;
                WriteLogDebug(DateTime.Now, $"{Name} with State RecoveryBuffer");
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
            if (!_managertoken.Token.IsCancellationRequested)
            {
                _blockrenewBuffer.Add(value);
            }
        }

        private void RemoveAllBuffer()
        {
            while (_availableBuffer.TryDequeue(out var value))
            {
                if (value is IDisposable disposablevalue)
                {
                    disposablevalue.Dispose();
                    WriteLogDebug(DateTime.Now, $"{Name} RemoveBuffer Disposed Item");
                }
                WriteLogDebug(DateTime.Now, $"{Name} RemoveBuffer removed Item");
            }
            _counterBuffer = 0;
            _counterAccquire = 0;
        }

        private void TryLoadBufferAsync(int diff)
        {
            var lockcount = new object();
            for (int i = 0; i < diff; i++)
            {
                using var ctstimeout = CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);
                ctstimeout.CancelAfter(FactoryTimeout);
                WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Handler Invoked");
                Task tk = null;
                try
                {
                    tk = Task.Run(() =>
                    {
                        var value = _factory.Invoke(ctstimeout.Token);
                        if (value is null)
                        {
                            _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                        }
                        else
                        {
                            WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Added New Item To Buffer");
                            _availableBuffer.Enqueue(value);
                            _counterBuffer++;
                            WriteLogTrace(DateTime.Now, $"{Name} TryLoadBufferAsync internalbuffer: {_availableBuffer.Count} , Available Count: {_counterBuffer} Unavailable Count: {_counterAccquire}");
                        }
                    }, ctstimeout.Token);
                    tk.Wait(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    //Send to retry
                    WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry (Timeout)");
                    _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                }
                catch (Exception ex)
                {
                    //Send error
                    WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Factory Exception");
                    _blockexceptionsBuffer.Add(new RingBufferException(Name, "TryLoadBufferAsync Factory Exception", ex));
                    //Send to retry
                    WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry (Timeout)");
                    _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                }
                finally
                {
                    tk?.Dispose();
                }
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

    }
}
