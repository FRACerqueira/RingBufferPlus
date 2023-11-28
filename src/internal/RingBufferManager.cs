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
    internal class RingBufferManager<T> : IRingBufferService<T>, IRingBufferWarmup<T>,IRingBufferSwith, IRingBufferCallback, IDisposable
    {
        private readonly ConcurrentQueue<T> _availableBuffer = new();
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
        private readonly IRingBufferSwith _swithTo;
        private readonly Func<T,bool> _factoryHealth;
        private readonly Func<CancellationToken, T> _factory;
        private readonly Action<ILogger?, RingBufferException> _errorHandler;
        private readonly Action<ScaleMode, ILogger, RingBufferMetric, CancellationToken> _reportHandler;
        private readonly object _lockcount = new();
        private readonly object _lockMetric = new();
        private readonly object _lockWarmup = new();
        private readonly SemaphoreSlim SemaphoreAquire = new(1, 1);

        private readonly CancellationToken _apptoken;
        private readonly CancellationTokenSource _managertoken;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _WarmupComplete;
        private bool _WarmupRunning;

        private int _currentCapacityBuffer;

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
            SampleUnit = ringBufferOptions.ScaleCapacityDelay/ ringBufferOptions.SampleUnit;
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
            _factoryHealth = ringBufferOptions.FactoryHealth;
            _errorHandler = ringBufferOptions.ErrorHandler;
            _reportHandler = ringBufferOptions.ReportHandler;
            _swithTo = ringBufferOptions.SwithTo;
            _swithFrom = null;
            IsSlave = ringBufferOptions.IsSlave;
            if (!IsSlave && _swithTo is not null)
            {
                ((IRingBufferCallback)_swithTo).CallBackMaster(this);
            }

            _currentCapacityBuffer = ringBufferOptions.Capacity;
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

        public string Name { get; }

        public int Capacity { get; }

        public int MinCapacity { get; }

        public int MaxCapacity { get; }

        public TimeSpan FactoryTimeout { get; }

        public TimeSpan FactoryIdleRetry  { get; }

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

        public RingBufferValue<T> Accquire(CancellationToken? cancellation = null)
        { 
            var localcancellation = cancellation?? CancellationToken.None;
            T result = default;
            var ok = false;

            //if not Warmup execute
            if (!_WarmupComplete)
            {
                Warmup(false, TimeSpan.Zero);
                WriteLogDebug(DateTime.Now, $"{Name} Wait Warmup Completed");
                while (!_WarmupComplete && !localcancellation.IsCancellationRequested)
                {
                    localcancellation.WaitHandle.WaitOne(2);
                }
                if (_availableBuffer.Count != Capacity)
                {
                    WriteLogDebug(DateTime.Now, $"{Name} Accquire Send Warmup Timeout to OnError Handler");
                    //Send error
                    _blockexceptionsBuffer.Add(new RingBufferException(Name, "Accquire Warmup Timeout"), _managertoken.Token);
                }
                else
                {
                    WriteLogInfo(DateTime.Now, $"{Name} Accquire Warmup Completed");
                }
                if (localcancellation.IsCancellationRequested)
                {
                    return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
                }
            }

            //try Accquire buffer
            var sw = Stopwatch.StartNew();
            while (!localcancellation.IsCancellationRequested && !ok)
            {
                if (!_availableBuffer.TryDequeue(out result))
                {
                    if (sw.Elapsed > AccquireTimeout)
                    {
                        break;
                    }
                    localcancellation.WaitHandle.WaitOne(2);
                }
                else
                {
                    if (_factoryHealth is not null && !_factoryHealth(result))
                    {
                        if (result is IDisposable disposablevalue)
                        {
                            disposablevalue.Dispose();
                            WriteLogDebug(DateTime.Now, $"{Name} Accquire Disposed Item");
                        }
                        _blockrenewBuffer.Add(new RingBufferValue<T>(1, ScaleMode.None));
                    }
                    else
                    {
                        ok = true;
                    }
                }
            }
            sw.Stop();

            //Accquire timeout
            if (!ok)
            {
                WriteLogDebug(DateTime.Now, $"{Name} Accquire timeout {sw.Elapsed}");
                //Send error
                _blockexceptionsBuffer.Add(new RingBufferException(Name, $"Accquire timeout {sw.Elapsed}, Current Capacity : {_currentCapacityBuffer}"), _managertoken.Token);
                return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
            }

            //send trigger mincapacity to default capacity
            if (!localcancellation.IsCancellationRequested &&
                _WarmupComplete &&
                ScaleCapacity && 
                ok && 
                _currentCapacityBuffer == MinCapacity && 
                _availableBuffer.Count <= TriggerFromMin)
            {
                SemaphoreAquire.Wait(_managertoken.Token);
                try
                {
                    //update _currentCapacityBuffer and send to consumer metric report
                    Task.Run(() =>
                    {
                        if (_currentCapacityBuffer == MinCapacity && _availableBuffer.Count >= TriggerFromMin)
                        {
                            WriteLogDebug(DateTime.Now, $"{Name} Accquire Invoked {ScaleMode.ToDefaultCapacity} : {Capacity}");
                            var diff = Capacity - _availableBuffer.Count;
                            Interlocked.Exchange(ref _currentCapacityBuffer, Capacity);
                            lock (_lockMetric)
                            {
                                _MetricBuffer.Clear();
                            }

                            _blockrenewBuffer.Add(new RingBufferValue<T>(diff, ScaleMode.ToDefaultCapacity));
                            SemaphoreAquire.Release();
                        }
                    }, _managertoken.Token);
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    SemaphoreAquire.Release();
                }
            }
            //update _currentCapacityBuffer and send trigger maxcapacity to default capacity
            else if (!localcancellation.IsCancellationRequested && 
                     ScaleCapacity &&
                     _WarmupComplete &&
                     ok && 
                     _currentCapacityBuffer == MaxCapacity && 
                     _availableBuffer.Count >= TriggerFromMax)
            {
                SemaphoreAquire.Wait(_managertoken.Token);
                try
                {
                    //send to consumer metric report
                    Task.Run(() =>
                    {
                        if (_currentCapacityBuffer == MaxCapacity && _availableBuffer.Count >= TriggerFromMax)
                        {
                            WriteLogDebug(DateTime.Now, $"{Name} Accquire Invoked {ScaleMode.ToDefaultCapacity} : {Capacity}");
                            var diff = _availableBuffer.Count - Capacity;
                            Interlocked.Exchange(ref _currentCapacityBuffer, Capacity);
                            lock (_lockMetric)
                            {
                                _MetricBuffer.Clear();
                            }
                            _blockrenewBuffer.Add(new RingBufferValue<T>(diff, ScaleMode.ToDefaultCapacity));
                            SemaphoreAquire.Release();
                        }
                    }, _managertoken.Token);
                }
                catch (OperationCanceledException)
                {
                    //none
                }
                finally
                {
                    SemaphoreAquire.Release();
                }
            }
            //ok
            return new RingBufferValue<T>(Name, sw.Elapsed, true, result,DisposeBuffer);
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

        #region IRingBufferSwith

        public bool SwithTo(ScaleMode scaleMode)
        {
            if (!_WarmupComplete)
            {
                return false;
            }
            if (_swithFrom is null)
            {
                throw  new InvalidOperationException($"{Name}: Not found Ring Buffer from Swith");
            }
            int diff;
            int newcap;
            switch (scaleMode)
            {
                case ScaleMode.None:
                    throw new InvalidOperationException($"{Name}: {scaleMode} Not valid to Swith");
                case ScaleMode.ToMinCapacity:
                    diff = _currentCapacityBuffer - MinCapacity;
                    newcap = MinCapacity;
                    break;
                case ScaleMode.ToMaxCapacity:
                    diff = MaxCapacity - _currentCapacityBuffer;
                    newcap = MaxCapacity;
                    break;
                case ScaleMode.ToDefaultCapacity:
                    diff = _currentCapacityBuffer - Capacity;
                    newcap = Capacity;
                    break;
                default:
                    throw new ArgumentException($"scaleMode Not found {scaleMode}");
            }
            if (diff != 0)
            {
                if (_reportHandler != null)
                {
                    WriteLogDebug(DateTime.Now, $"{Name} SwithTo Invoked {SourceTrigger.MasterSlave} : {scaleMode} and Send Metric To Report Thread");
                    _blockreportBuffer.Add((scaleMode, new RingBufferMetric(SourceTrigger.MasterSlave, _currentCapacityBuffer, newcap)));
                }
                _currentCapacityBuffer = newcap;
                WriteLogDebug(DateTime.Now, $"{Name} SwithTo Send Message Create to Renew Buffer Thread");
                _blockrenewBuffer.Add(new RingBufferValue<T>(diff, scaleMode));
                lock (_lockMetric)
                {
                    _MetricBuffer.Clear();
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
                                if (item.SkipTurnback)
                                {
                                    if (item.Current is not null && item.Current is IDisposable disposable)
                                    {
                                        disposable.Dispose();
                                        WriteLogDebug(DateTime.Now, $"{Name} Renew Buffer Disposed Item");
                                    }
                                    _blockrenewBuffer.Add(new RingBufferValue<T>(1, ScaleMode.None));
                                }
                                else if (item.IsScaleCapacity)
                                {
                                    if (_swithTo is not null && item.ScaleMode != ScaleMode.None)
                                    {

                                        WriteLogDebug(DateTime.Now, $"{Name} SemaphoremasterSlave wait");
                                        SemaphoremasterSlave.Wait();
                                        WriteLogDebug(DateTime.Now, $"{Name} SemaphoremasterSlave done");

                                        var slavename = ((IRingBufferCallback)_swithTo).Name;
                                        WriteLogDebug(DateTime.Now, $"Master({Name}) to Slave({slavename}) swith to {item.ScaleMode}");
                                        if (_swithTo.SwithTo(item.ScaleMode))
                                        {
                                            WriteLogDebug(DateTime.Now, $"{Name} SemaphoremasterSlave wait {slavename} Release");
                                            SemaphoremasterSlave.Wait();
                                            var diff = _currentCapacityBuffer - _availableBuffer.Count;
                                            RemoveBuffer(_currentCapacityBuffer);
                                            await TryLoadBufferAsync(diff);
                                            if (_currentCapacityBuffer == MinCapacity && _swithTo is not null && _MetricBuffer.Count == 0)
                                            {
                                                //clear all buffer
                                                RemoveBuffer(0);
                                                //add buffer (MinCapacity)
                                                await TryLoadBufferAsync(MinCapacity);
                                            }
                                        }
                                        if (SemaphoremasterSlave.CurrentCount == 0)
                                        {
                                            SemaphoremasterSlave.Release();
                                        }
                                    }
                                    else
                                    {
                                        var diff = _currentCapacityBuffer - _availableBuffer.Count;
                                        RemoveBuffer(_currentCapacityBuffer);    
                                        await TryLoadBufferAsync(diff);
                                        if (diff != 0 && _swithFrom is not null && ((IRingBufferCallback)_swithFrom).SemaphoremasterSlave.CurrentCount == 0)
                                        { 
                                            var master = ((IRingBufferCallback)_swithFrom).Name;
                                            WriteLogDebug(DateTime.Now, $"{Name}: From Master({master}) SemaphoremasterSlave Release");
                                            ((IRingBufferCallback)_swithFrom).SemaphoremasterSlave.Release();
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (!item.SkipTurnback && !item.IsScaleCapacity && RehydrateBuffer(item.Current))
                                {
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
                            WriteLogDebug(DateTime.Now, $"{Name} Retry Factory Send Message Create to Renew Buffer Thread");
                            _blockrenewBuffer.Add(new RingBufferValue<T>(1, ScaleMode.None));
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
                    _managertoken.Token.WaitHandle.WaitOne(10);
                }
                while (ScaleCapacity && !_managertoken.Token.IsCancellationRequested)
                {
                    _managertoken.Token.WaitHandle.WaitOne(SampleUnit);
                    if (_managertoken.Token.IsCancellationRequested)
                    {
                        continue;
                    }
                    lock (_lockMetric)
                    {
                        var available = _availableBuffer.Count;
                        _MetricBuffer.Add(available);
                        WriteLogDebug(DateTime.Now, $"{Name} Metric Added Available information: {available}, {_MetricBuffer.Count}/{SamplesCount}");
                        if (_MetricBuffer.Count >= SamplesCount)
                        {
                            var avg = _MetricBuffer.ToArray().Average();
                            _MetricBuffer.Clear();
                            if (!_managertoken.Token.IsCancellationRequested)
                            {
                                _blockScaleBuffer.Add(Convert.ToInt32(avg));
                                WriteLogDebug(DateTime.Now, $"{Name} Metric Resume Created({Convert.ToInt32(avg)})");
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
                            var newcap = _currentCapacityBuffer;
                            var currentcap = newcap;
                            var diff = 0;
                            ScaleMode? mode = null;
                            if (ScaleToMax.HasValue && freebuffer <= ScaleToMax.Value && currentcap == Capacity)
                            {
                                newcap = MaxCapacity;
                                mode = ScaleMode.ToMaxCapacity;
                                diff = newcap - _availableBuffer.Count;
                            }
                            else if (ScaleToMin.HasValue && freebuffer >= ScaleToMin.Value && currentcap == Capacity)
                            {
                                newcap = MinCapacity;
                                mode = ScaleMode.ToMinCapacity;
                                diff = currentcap - _availableBuffer.Count - newcap;
                            }
                            else if (RollbackFromMin.HasValue && freebuffer <= RollbackFromMin.Value && currentcap == MinCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                                diff = newcap - _availableBuffer.Count;
                            }
                            else if (RollbackFromMax.HasValue && freebuffer >= RollbackFromMax.Value && currentcap == MaxCapacity)
                            {
                                newcap = Capacity;
                                mode = ScaleMode.ToDefaultCapacity;
                                diff = _availableBuffer.Count - newcap;
                            }
                            if (mode.HasValue && !_managertoken.Token.IsCancellationRequested)
                            {
                                if (_reportHandler != null)
                                {
                                    WriteLogDebug(DateTime.Now, $"{Name} Scale Capacity Invoked {mode} : {newcap} and Send Metric To Report Thread");
                                    _blockreportBuffer.Add((mode.Value, new RingBufferMetric(SourceTrigger.AutoScale, currentcap, newcap)));
                                }
                                _currentCapacityBuffer = newcap;
                                WriteLogDebug(DateTime.Now, $"{Name} Scale Capacity Send Message Create to Renew Buffer Thread");
                                _blockrenewBuffer.Add(new RingBufferValue<T>(diff,mode.Value));
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

            using var warmupcts =  CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);

            WriteLogInfo(DateTime.Now, $"{Name} Creating {Capacity} items");

            _blockrenewBuffer.Add(new RingBufferValue<T>(Capacity, ScaleMode.None));

            warmupcts.CancelAfter(timeoutfullcapacity);
            if (waitfullcapacity)
            {

                while (!warmupcts.Token.IsCancellationRequested && _availableBuffer.Count != _currentCapacityBuffer)
                {
                    warmupcts.Token.WaitHandle.WaitOne(10);
                }
            }
            else
            {
                while (!warmupcts.Token.IsCancellationRequested && _availableBuffer.Count >= 2)
                {
                    warmupcts.Token.WaitHandle.WaitOne(10);
                }
            }

            WriteLogInfo(DateTime.Now, $"{Name} Created {_availableBuffer.Count} items");

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
            else
            {
                if (value.Current is IDisposable disposablevalue)
                {
                    disposablevalue.Dispose();
                    WriteLogDebug(DateTime.Now, $"{Name} Renew Buffer Disposed Item");
                }
            }
        }

        private void RemoveBuffer(int newcapacity)
        {
            while (_availableBuffer.Count > newcapacity)
            {
                if (_availableBuffer.TryDequeue(out var value))
                {
                    if (value is IDisposable disposablevalue)
                    {
                        disposablevalue.Dispose();
                        WriteLogDebug(DateTime.Now, $"{Name} RemoveBuffer Disposed Item");
                    }
                    WriteLogDebug(DateTime.Now, $"{Name} RemoveBuffer removed Item");
                }
            }
        }

        private async Task TryLoadBufferAsync(int diff)
        {
            for (int i = 0; i < diff; i++)
            {
                using var ctstimeout = CancellationTokenSource.CreateLinkedTokenSource(_managertoken.Token);
                ctstimeout.CancelAfter(FactoryTimeout);
                WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Handler Invoked");
                try
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            var value = _factory.Invoke(ctstimeout.Token);
                            if (value != null && RehydrateBuffer(value))
                            {
                                WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Added New Item To Buffer");
                            }
                        }
                        catch (Exception ex)
                        {

                            WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Exception to OnError Handler");
                            //error
                            if (!_managertoken.Token.IsCancellationRequested)
                            {
                                _blockexceptionsBuffer.Add(new RingBufferException(Name, "TryLoadBufferAsync Factory Error", ex));
                            }
                            //timeout
                            WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry");
                            if (!_managertoken.Token.IsCancellationRequested)
                            {
                                _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryIdleRetry));
                            }
                        }

                    }, ctstimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ctstimeout.IsCancellationRequested && !_managertoken.IsCancellationRequested)
                    {
                        WriteLogDebug(DateTime.Now, $"{Name}TryLoadBufferAsync Send Timeout to OnError Handler");
                        //Send error
                        _blockexceptionsBuffer.Add(new RingBufferException(Name, "TryLoadBufferAsync Factory Timeout, Send Factory to Retry"));
                        //Send to retry
                        WriteLogDebug(DateTime.Now, $"{Name} TryLoadBufferAsync Send Factory to Retry");
                        _blockRetryFactoryBuffer.Add(DateTime.Now.Add(FactoryTimeout));
                    }
                }
            }
        }

        private void WriteLogDebug(DateTime dtref, string message)
        {
            _logger?.Log( LogLevel.Debug, "[{dtref}] {message}", dtref,message);
        }

        private void WriteLogInfo(DateTime dtref, string message)
        {
            _logger?.Log(LogLevel.Information, "[{dtref}] {message}", dtref, message);
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
                WriteLogDebug(DateTime.Now, $"{Name} RehydrateBuffer Disposed Item");
            }
            return false;
        }

    }
}
