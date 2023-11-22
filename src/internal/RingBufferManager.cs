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
    internal class RingBufferManager<T> : IRingBufferService<T>, IRingBufferWarmup<T>, IDisposable
    {
        private readonly ConcurrentQueue<T> _availableBuffer = new();
        private readonly BlockingCollection<RingBufferValue<T>> _blockrenewBuffer = [];
        private readonly BlockingCollection<RingBufferException> _blockexceptionsBuffer = [];
        private readonly BlockingCollection<DateTime> _blockRetryFactoryBuffer = [];
        private readonly List<int> _MetricBuffer = [];
        private readonly BlockingCollection<(ScaleMode, RingBufferMetric)> _blockreportBuffer = [];
        private readonly BlockingCollection<int> _blockScaleBuffer = [];

        private Task _renewBufferThread;
        private Task _retryFactoryThread;
        private Task _logErrorBufferThread;
        private Task _metricBufferThread;
        private Task _scaleCapacityThread;
        private Task _reportscaleCapacityThread;

        private readonly SemaphoreSlim SemaphoreAquire = new(1, 1);
        private readonly object _lockcount = new();
        private readonly object _lockMetric = new();
        private readonly object _lockWarmup = new();
        private readonly IRingBufferOptions<T> _ringBufferOptions;
        private readonly CancellationToken _apptoken;
        private volatile int _currentCapacityBuffer;
        private readonly CancellationTokenSource _managertoken;

        private bool _disposed;
        private bool _WarmupComplete;
        private bool _WarmupRunning;

        private int _available;
        private int _unavailable;
        private int _toCreating;

        public RingBufferManager(IRingBufferOptions<T> ringBufferOptions, CancellationToken cancellationToken)
        {
            _apptoken = cancellationToken;
            _apptoken.Register(() => Dispose(true));
            _ringBufferOptions = ringBufferOptions;
            _currentCapacityBuffer = ringBufferOptions.Capacity;
            _managertoken = CancellationTokenSource.CreateLinkedTokenSource(_apptoken);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Capacity : {_ringBufferOptions.Capacity}"));
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} MinCapacity : {_ringBufferOptions.MinCapacity}"));
            if (_ringBufferOptions.ScaleToMinGreaterEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale down (Capacity->Min) Greater-Eq: {_ringBufferOptions.ScaleToMinGreaterEq.Value}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale down (Capacity->Min) Greater-Eq: Null"));
            }
            if (_ringBufferOptions.MinRollbackWhenFreeLessEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Rollback (Min->Capacity) Less-Eq : {_ringBufferOptions.MinRollbackWhenFreeLessEq}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Rollback (Min->Capacity) Less-Eq : Null"));
            }
            if (_ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Trigger (Min->Capacity) Accq. Less-Eq : {_ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Trigger (Min->Capacity) Accq. Less-Eq : Null"));
            }
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} MaxCapacity : {_ringBufferOptions.MaxCapacity}"));
            if (_ringBufferOptions.ScaleToMaxLessEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale up (Capacity->Max) Less-Eq: {_ringBufferOptions.ScaleToMaxLessEq}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale up (Capacity->Max) Less-Eq: Null"));
            }
            if (_ringBufferOptions.MaxRollbackWhenFreeGreaterEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Rollback (Max->Capacity) Greater-Eq : {_ringBufferOptions.MaxRollbackWhenFreeGreaterEq}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Rollback (Max->Capacity) Greater-Eq : Null"));
            }
            if (_ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq.HasValue)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Trigger (Max->Capacity) Accq. Greater-Eq : {_ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq}"));
            }
            else
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Trigger (Max->Capacity) Accq. Greater-Eq : Null"));
            }
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Unit Time: {_ringBufferOptions.ScaleCapacityDelay}"));
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Unit Samples: {_ringBufferOptions.SampleUnit}"));
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Sample Delay: {_ringBufferOptions.SampleDelay}"));
        }

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
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
                SemaphoreAquire.Dispose();
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

        #region IRingBufferService

        public string Name => _ringBufferOptions.Name;

        public int Capacity => _ringBufferOptions.Capacity;

        public int MinCapacity => _ringBufferOptions.MinCapacity;

        public int MaxCapacity => _ringBufferOptions.MaxCapacity;

        public TimeSpan FactoryTimeout => _ringBufferOptions.FactoryTimeout;

        public TimeSpan FactoryIdleRetry => _ringBufferOptions.FactoryIdleRetryError;

        public bool ScaleCapacity => _ringBufferOptions.ScaleToMaxLessEq.HasValue || _ringBufferOptions.ScaleToMinGreaterEq.HasValue;

        public TimeSpan SampleUnit => _ringBufferOptions.ScaleCapacityDelay;

        public int SamplesCount => _ringBufferOptions.SampleUnit;

        public int? ScaleToMin => _ringBufferOptions.ScaleToMinGreaterEq;

        public int? RollbackFromMin => _ringBufferOptions.MinRollbackWhenFreeLessEq;

        public int? TriggerFromMin => _ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq;

        public int? ScaleToMax => _ringBufferOptions.ScaleToMaxLessEq;

        public int? RollbackFromMax => _ringBufferOptions.MaxRollbackWhenFreeGreaterEq;

        public int? TriggerFromMax => _ringBufferOptions.MaxTriggerByAccqWhenFreeGreaterEq;

        public TimeSpan AccquireTimeout => _ringBufferOptions.AccquireTimeout;


        public void Counters(Action<int, int, int> counters)
        {
            lock (_lockcount)
            {
                var cur = _currentCapacityBuffer;
                var ava = _availableBuffer.Count;
                _available = ava;
                _unavailable = cur - ava;
                _toCreating = cur - (_available + _unavailable);
                counters(_available, _unavailable, _toCreating);
            }
        }


        public RingBufferValue<T> Accquire(CancellationToken? cancellation = null)
        { 
            var localcancellation = cancellation?? CancellationToken.None;
            T result = default;
            var ok = false;

            Warmup(false, TimeSpan.Zero);

            if (!_WarmupComplete)
            {
                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Wait Warmup Completed"));
                while (!_WarmupComplete && !localcancellation.IsCancellationRequested)
                {
                    localcancellation.WaitHandle.WaitOne(100);
                }
                if (_availableBuffer.Count != _ringBufferOptions.Capacity)
                {
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Accquire Send Warmup Timeout to OnError Handler"));
                    //Send error
                    _blockexceptionsBuffer.Add(new RingBufferException(_ringBufferOptions.Name, "Accquire Warmup Timeout"), _managertoken.Token);
                }
                else
                {
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Accquire Warmup Completed"));
                }
            }


            if (localcancellation.IsCancellationRequested)
            {
                return new RingBufferValue<T>(_ringBufferOptions.Name, TimeSpan.Zero, ok, result,RenewBuffer);
            }

            var sw = Stopwatch.StartNew();
            var max = _ringBufferOptions.AccquireTimeout;
            while (!localcancellation.IsCancellationRequested && !ok)
            {
                if (!_availableBuffer.TryDequeue(out result))
                {
                    if (sw.Elapsed > max)
                    {
                        break;
                    }
                    localcancellation.WaitHandle.WaitOne(2);
                }
                else
                {
                    ok = true;
                }
            }
            sw.Stop();
            if (localcancellation.IsCancellationRequested && ok)
            {
                _availableBuffer.Enqueue(result);
            }
            if (!localcancellation.IsCancellationRequested && _ringBufferOptions.HasScaleCapacity &&  ok && _currentCapacityBuffer == _ringBufferOptions.MinCapacity && _availableBuffer.Count <= _ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq)
            {
                try
                {
                    SemaphoreAquire.Wait(_managertoken.Token);
                    Task.Run(() =>
                    {
                        if (_currentCapacityBuffer == _ringBufferOptions.MinCapacity && _availableBuffer.Count >= _ringBufferOptions.MinTriggerByAccqWhenFreeGreaterEq)
                        {
                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Accquire Invoked {ScaleMode.ToDefaultCapacity} : {_ringBufferOptions.Capacity}"));
                            lock (_lockMetric)
                            {
                                _MetricBuffer.Clear();
                                var diff = _ringBufferOptions.Capacity - _availableBuffer.Count; 
                                _currentCapacityBuffer = _ringBufferOptions.Capacity;
                                _blockrenewBuffer.Add(new RingBufferValue<T>(diff));
                            }
                            SemaphoreAquire.Release();
                        }
                    }, _managertoken.Token);
                }
                catch (OperationCanceledException)
                {
                    SemaphoreAquire.Release();
                }
            }
            if (!localcancellation.IsCancellationRequested && _ringBufferOptions.HasScaleCapacity && ok && _currentCapacityBuffer == _ringBufferOptions.MaxCapacity && _availableBuffer.Count >= _ringBufferOptions.MaxTriggerByAccqWhenFreeGreaterEq)
            {
                try
                {
                    SemaphoreAquire.Wait(_managertoken.Token);
                    Task.Run(() =>
                    {
                        if (_currentCapacityBuffer == _ringBufferOptions.MaxCapacity && _availableBuffer.Count >= _ringBufferOptions.MaxTriggerByAccqWhenFreeGreaterEq)
                        {
                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Accquire Invoked {ScaleMode.ToDefaultCapacity} : {_ringBufferOptions.Capacity}"));
                            lock (_lockMetric)
                            {
                                _MetricBuffer.Clear();
                                var diff = _availableBuffer.Count - _ringBufferOptions.Capacity;
                                _currentCapacityBuffer = _ringBufferOptions.Capacity;
                                _blockrenewBuffer.Add(new RingBufferValue<T>(diff));
                            }
                            SemaphoreAquire.Release();
                        }
                    }, _managertoken.Token);
                }
                catch (OperationCanceledException)
                {
                    SemaphoreAquire.Release();
                }
            }
            if (!ok)
            {
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Accquire fail after {sw.Elapsed}"));
                //Send error
                _blockexceptionsBuffer.Add(new RingBufferException(_ringBufferOptions.Name, $"Accquire fail after { sw.Elapsed }, Current Capacity : {_currentCapacityBuffer}"), _managertoken.Token);
            }
            return new RingBufferValue<T>(_ringBufferOptions.Name, sw.Elapsed, ok, result,RenewBuffer);
        }

        #endregion

        #region IRingBufferWarmup

        public bool Warmup(TimeSpan? timeout = null)
        {
            var tm = timeout ?? TimeSpan.FromSeconds(30); ;
            Warmup(true, tm);
            return _availableBuffer.Count == _currentCapacityBuffer;

        }

        #endregion

        private void Warmup(bool waitfullcapacity, TimeSpan timeoutfullcapacity)
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

            _renewBufferThread = new Task(async () =>
            {
                try
                {
                    foreach (var item in _blockrenewBuffer.GetConsumingEnumerable(_managertoken.Token))
                    {

                        if (item.Successful || item.IsScaleCapacity)
                        {
                            if (item.SkipTurnback || item.IsScaleCapacity)
                            {
                                if (item.Current is IDisposable disposable)
                                {
                                    disposable.Dispose();
                                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Disposed Item"));
                                }
                                if (item.SkipTurnback)
                                {
                                    _blockrenewBuffer.Add(new RingBufferValue<T>(1));
                                }
                                else if (item.IsScaleCapacity)
                                {
                                    using var ctstimeout = CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);
                                    try
                                    {
                                        if (item.DiffCapacity > 0)
                                        {
                                            for (int i = 0; i < item.DiffCapacity; i++)
                                            {
                                                ctstimeout.CancelAfter(_ringBufferOptions.FactoryTimeout);
                                                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Factory Handler Invoked"));
                                                await Task.Run(() =>
                                                {
                                                    var value = _ringBufferOptions.FactoryHandler.Invoke(ctstimeout.Token);
                                                    if (RehydrateBuffer(value))
                                                    {
                                                        WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Added New Item To Buffer"));
                                                    }
                                                }, ctstimeout.Token);
                                            }
                                        }
                                        else
                                        {
                                            for (int i = 0; i < item.DiffCapacity * -1; i++)
                                            {
                                                _availableBuffer.TryDequeue(out var value);
                                                if (value is IDisposable disposablevalue)
                                                {
                                                    disposablevalue.Dispose();
                                                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Disposed Item"));
                                                }
                                                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer removed Item"));
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        if (ctstimeout.IsCancellationRequested && !_managertoken.IsCancellationRequested)
                                        {
                                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Send Timeout to OnError Handler"));
                                            //Send error
                                            _blockexceptionsBuffer.Add(new RingBufferException(_ringBufferOptions.Name, "Renew Buffer Factory Timeout, Send Factory to Retry"));
                                            //Send to retry
                                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Send Factory to Retry"));
                                            _blockRetryFactoryBuffer.Add(DateTime.Now.Add(_ringBufferOptions.FactoryTimeout));
                                        }
                                    }
                                    catch (Exception ex)
                                    {

                                        WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Send Exception to OnError Handler"));
                                        //error
                                        if (!_managertoken.Token.IsCancellationRequested)
                                        {
                                            _blockexceptionsBuffer.Add(new RingBufferException(_ringBufferOptions.Name, "Renew Buffer Factory Error", ex));
                                        }
                                        //timeout
                                        WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Send Factory to Retry"));
                                        if (!_managertoken.Token.IsCancellationRequested)
                                        {
                                            _blockRetryFactoryBuffer.Add(DateTime.Now.Add(_ringBufferOptions.FactoryTimeout));
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (RehydrateBuffer(item.Current))
                                {
                                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Rehydrated Buffer"));
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
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Thread Stoped"));
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Thread Created"));
            _renewBufferThread.Start();

            _logErrorBufferThread = new Task(() =>
            {
                try
                {
                    foreach (var item in _blockexceptionsBuffer.GetConsumingEnumerable(_managertoken.Token))
                    {
                        try
                        {
                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} OnError Handler Invoked"));
                            _ringBufferOptions.ErrorHandler?.Invoke(_ringBufferOptions.Logger, item);
                        }
                        catch (Exception ex)
                        {
                            var dtref = DateTime.Now;
                            _ringBufferOptions.Logger?.Log(LogLevel.Error, "[{dtref}] Error again! OnError Handler : {ex}", dtref, ex);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Log Error Buffer Thread Stoped"));
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Log Error Buffer Thread Created"));
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
                        WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Wait Retry Factory {diff}"));
                        _managertoken.Token.WaitHandle.WaitOne(diff);
                        if (!_managertoken.Token.IsCancellationRequested)
                        {
                            WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Retry Factory Send Message Create to Renew Buffer Thread"));
                            _blockrenewBuffer.Add(new RingBufferValue<T>(10));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Retry Factory Thread Stoped"));
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Retry Factory Thread Created"));
            _retryFactoryThread.Start();

            _metricBufferThread = new Task(() =>
            {
                while (_ringBufferOptions.HasScaleCapacity && !_managertoken.Token.IsCancellationRequested)
                {
                    _managertoken.Token.WaitHandle.WaitOne(_ringBufferOptions.SampleDelay);
                    if (_managertoken.Token.IsCancellationRequested)
                    {
                        continue;
                    }
                    lock (_lockMetric)
                    {
                        var available = _availableBuffer.Count;
                        _MetricBuffer.Add(available);
                        WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Metric Added Available information: {available}, {_MetricBuffer.Count}/{_ringBufferOptions.SampleUnit}"));
                        if (_MetricBuffer.Count >= _ringBufferOptions.SampleUnit)
                        {
                            var avg = _MetricBuffer.ToArray().Average();
                            _MetricBuffer.Clear();
                            if (!_managertoken.Token.IsCancellationRequested)
                            {
                                _blockScaleBuffer.Add(Convert.ToInt32(avg));
                                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Metric Resume Created({Convert.ToInt32(avg)})"));
                            }
                        }
                    }
                }
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Metric Buffer Thread Stoped"));
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Metric Buffer Thread Created"));
            _metricBufferThread.Start();

            _scaleCapacityThread = new Task(() =>
            {
                if (_ringBufferOptions.HasScaleCapacity)
                {
                    try
                    {
                        foreach (var item in _blockScaleBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            var newcap = _currentCapacityBuffer;
                            var currentcap = newcap;
                            var diff = 0;
                            ScaleMode? mode = null;
                            if (_ringBufferOptions.ScaleToMaxLessEq.HasValue && item <= _ringBufferOptions.ScaleToMaxLessEq.Value && currentcap == _ringBufferOptions.Capacity)
                            {
                                newcap = _ringBufferOptions.MaxCapacity;
                                mode = ScaleMode.ToMaxCapacity;
                                diff = newcap - _availableBuffer.Count;
                            }
                            else if (_ringBufferOptions.ScaleToMinGreaterEq.HasValue && item >= _ringBufferOptions.ScaleToMinGreaterEq.Value && currentcap == _ringBufferOptions.Capacity)
                            {
                                newcap = _ringBufferOptions.MinCapacity;
                                mode = ScaleMode.ToMinCapacity;
                                diff = (currentcap - _availableBuffer.Count) - newcap;
                            }
                            else if (_ringBufferOptions.MinRollbackWhenFreeLessEq.HasValue && item <= _ringBufferOptions.MinRollbackWhenFreeLessEq.Value && currentcap == _ringBufferOptions.MinCapacity)
                            {
                                newcap = _ringBufferOptions.Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                                diff = newcap - _availableBuffer.Count;
                            }
                            else if (_ringBufferOptions.MaxRollbackWhenFreeGreaterEq.HasValue && item >= _ringBufferOptions.MaxRollbackWhenFreeGreaterEq.Value && currentcap == _ringBufferOptions.MaxCapacity)
                            {
                                newcap = _ringBufferOptions.Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                                diff = _availableBuffer.Count - newcap;
                            }
                            if (mode.HasValue)
                            {
                                if (!_managertoken.Token.IsCancellationRequested)
                                {
                                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale Capacity Invoked {mode} : {newcap} and Send Metric To Report Thread"));
                                    _blockreportBuffer.Add((mode.Value, new RingBufferMetric(SourceTrigger.AutoScale, currentcap, newcap, _ringBufferOptions.Capacity, _ringBufferOptions.MinCapacity, _ringBufferOptions.MaxCapacity, item, DateTime.Now)));
                                    _currentCapacityBuffer = newcap;
                                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale Capacity Send Message Create to Renew Buffer Thread"));
                                    _blockrenewBuffer.Add(new RingBufferValue<T>(diff));
                                    lock (_lockMetric)
                                    {
                                        _MetricBuffer.Clear();
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //none
                    }
                }
                WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale Capacity Thread Stoped"));
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Scale Capacity Thread Created"));
            _scaleCapacityThread.Start();

            _reportscaleCapacityThread = new Task(() =>
            {
                try
                {
                    if (_ringBufferOptions.HasScaleCapacity)
                    {
                        foreach ((ScaleMode Mode, RingBufferMetric Metric) in _blockreportBuffer.GetConsumingEnumerable(_managertoken.Token))
                        {
                            try
                            {
                                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} ReportHandler Invoked"));
                                _ringBufferOptions.ReportHandler?.Invoke(Mode, _ringBufferOptions.Logger, Metric, _managertoken.Token);
                            }
                            catch (Exception ex)
                            {
                                var dtref = DateTime.Now;
                                _ringBufferOptions.Logger?.Log(LogLevel.Error, "[{dtref}] Error ReportScale Handler : {ex}", dtref, ex);
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
                    WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Report Scale Capacity Thread Stoped"));
                }
            }, _managertoken.Token);
            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Report Scale Capacity Thread Created"));
            _reportscaleCapacityThread.Start();

            using var warmupcts =  CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);

            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Creating {_ringBufferOptions.Capacity} items"));

            _blockrenewBuffer.Add(new RingBufferValue<T>(_ringBufferOptions.Capacity));

            warmupcts.CancelAfter(timeoutfullcapacity);
            if (waitfullcapacity)
            {

                while (!warmupcts.Token.IsCancellationRequested && _availableBuffer.Count != _currentCapacityBuffer)
                {
                    warmupcts.Token.WaitHandle.WaitOne(100);
                }
            }
            else
            {
                while (!warmupcts.Token.IsCancellationRequested && _availableBuffer.Count >= 2)
                {
                    warmupcts.Token.WaitHandle.WaitOne(100);
                }
            }

            WriteLogInfo(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Created {_availableBuffer.Count} items"));

            lock (_lockWarmup)
            {
                _WarmupComplete = true;
                _WarmupRunning = false;
            }
        }

        private void RenewBuffer(RingBufferValue<T> value)
        {
            if (!_managertoken.IsCancellationRequested)
            {
                _blockrenewBuffer.Add(value);
            }
            else
            {
                if (value is IDisposable disposablevalue)
                {
                    disposablevalue.Dispose();
                    WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} Renew Buffer Disposed Item"));
                }
            }
        }

        private void WriteLogDebug(DateTime dtref, string message)
        {
            _ringBufferOptions.Logger?.Log( LogLevel.Debug, "[{dtref}] {message}", dtref,message);
        }

        private void WriteLogInfo(DateTime dtref, string message)
        {
            _ringBufferOptions.Logger?.Log(LogLevel.Information, "[{dtref}] {message}", dtref, message);
        }

        private bool RehydrateBuffer(T value)
        {
            if (_availableBuffer.Count < _currentCapacityBuffer)
            {
                _availableBuffer.Enqueue(value);
                return true;
            }
            if (value is IDisposable disposable)
            {
                disposable.Dispose();
                WriteLogDebug(DateTime.Now, string.Format($"{_ringBufferOptions.Name} RehydrateBuffer Disposed Item"));
            }
            return false;
        }
    }
}
