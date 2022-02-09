using Microsoft.Extensions.Logging;
using RingBufferPlus.Events;
using RingBufferPlus.Exceptions;
using RingBufferPlus.ObjectValues;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus.Features
{
    internal class ManagerRingBuffer<T> : IDisposable
    {
        private readonly string _alias;
        private readonly int _mincapacity;
        private readonly int _maxcapacity;
        private readonly ConcurrentQueue<T> _availableBuffer = new();
        private readonly RingBufferCount _autoScalerCount = new();
        private readonly RingBufferCount _reportCount = new();
        private readonly ILogger _logger = null;
        private readonly LogLevel _defaultloglevel;
        private readonly CancellationToken _stoptoken;
        private readonly Func<bool> _linkedFailureState;
        private readonly object _sync = new object();

        private readonly Thread _renewBufferThread;
        private readonly BlockingCollection<BackOfficeDisposedBuffer> blockrenewBuffer = new();

        private Thread _reportBufferThread;
        private Thread _healthCheckThread;
        private Thread _eventErrorThread;
        private Thread _eventTimeoutThread;
        private Thread _eventAutoScalerThread;
        private Thread _autoscalerBufferThread;
        private Thread _redefineCapacityThread;


        private readonly BlockingCollection<Tuple<DateTime, Exception>> blockeventError = new();
        private readonly BlockingCollection<Tuple<DateTime, RingBufferTimeoutEventArgs>> blockeventTimeout = new();
        private readonly BlockingCollection<Tuple<DateTime, RingBufferAutoScaleEventArgs>> blockeventAutoScaler = new();
        private readonly BlockingCollection<Tuple<DateTime, int?>> blockAutoScalerBuffer = new();


        private volatile int _runningCount;
        private volatile bool _failureState;
        private volatile int _starting;

        private bool _disposedValue;

        private class BackOfficeDisposedBuffer
        {
            public T Buffer { get; set; }
            public bool SucceededAccquire { get; set; }
            public bool SkipTurnback { get; set; }
            public Exception Error { get; set; }
        }

        public ManagerRingBuffer(string alias, int min, int max, ILogger logger, LogLevel defaultloglevel, Func<bool> linkedFailureState, CancellationToken cancellationToken)
        {
            _alias = alias;
            _mincapacity = min;
            _maxcapacity = max;
            _logger = logger;
            _defaultloglevel = defaultloglevel;
            _stoptoken = cancellationToken;
            _linkedFailureState = linkedFailureState;

            WriteLog(DateTime.Now, $"{_alias} RenewBuffer Thread Created ");
            _renewBufferThread = new Thread(() =>
            {
                try
                {
                    WriteLog(DateTime.Now, $"{_alias} RenewBuffer Thread Running");
                    foreach (var item in blockrenewBuffer.GetConsumingEnumerable(_stoptoken))
                    {
                        var newava = 0;
                        var newrun = 0;
                        if (item.SucceededAccquire)
                        {
                            newrun = Interlocked.Decrement(ref _runningCount);
                            if (item.SkipTurnback)
                            {
                                lock (_sync)
                                {
                                    newava = _availableBuffer.Count;
                                }
                                if (item.Buffer is IDisposable disposable)
                                {
                                    disposable.Dispose();
                                }
                                if (item.Error != null)
                                {
                                    IncrementError();
                                    if (!blockeventError.IsAddingCompleted)
                                    {
                                        blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException("Client Accquire Error", item.Error)));
                                    }
                                }
                            }
                            else
                            {
                                lock (_sync)
                                {
                                    _availableBuffer.Enqueue(item.Buffer);
                                    newava = _availableBuffer.Count;
                                }
                            }
                        }
                        else
                        {
                            lock (_sync)
                            {
                                newava = _availableBuffer.Count;
                                newrun = _runningCount;
                            }
                        }
                        _failureState = newava + newrun < 2;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    WriteLog(DateTime.Now, $"{_alias} RenewBuffer Thread Stoped");
                }
            });
            _renewBufferThread.IsBackground = true;
            _renewBufferThread.Start();
        }

        public void UsingRedefineCapacity(Func<CancellationToken, T> factorySync, Func<CancellationToken, Task<T>> factoryAsync, TimeSpan WaitNext)
        {
            WriteLog(DateTime.Now, $"{_alias} RedefineCapacity Thread Created");
            _redefineCapacityThread = new Thread(async () =>
            {
                try
                {
                    WriteLog(DateTime.Now, $"{_alias} RedefineCapacity Thread Running");
                    foreach (var item in blockAutoScalerBuffer.GetConsumingEnumerable(_stoptoken))
                    {
                        var localmetric = CreateMetric(WaitNext);
                        var localtarget = item.Item2 ?? localmetric.State.MinimumCapacity;
                        var oldvalue = localmetric.State.CurrentCapacity;


                        if (localtarget == oldvalue)
                        {
                            continue;
                        }
                        if (localmetric.State.FailureState)
                        {
                            if (NaturalTimer.Delay(TimeSpan.FromSeconds(10), _stoptoken))
                            {
                                continue;
                            }
                        }
                        var diff = localtarget - oldvalue;
                        if (item.Item2.HasValue)
                        {
                            WriteLog(item.Item1, $"{_alias} Try AutoScale({diff}) {oldvalue} to {oldvalue + diff}.");
                        }
                        else
                        {
                            WriteLog(item.Item1, $"{_alias} Try AutoScale({diff}) {oldvalue} to {item.Item2}.");
                        }

                        if (diff < 0)
                        {
                            while (diff < 0 && !_stoptoken.IsCancellationRequested)
                            {
                                if (localmetric.State.CurrentCapacity <= localmetric.State.MinimumCapacity)
                                {
                                    break;
                                }
                                if (_availableBuffer.TryDequeue(out T deletebuffer))
                                {
                                    if (deletebuffer is IDisposable disposable)
                                    {
                                        disposable.Dispose();
                                    }
                                    diff++;
                                }
                                else
                                {
                                    break;
                                }
                                localmetric = CreateMetric(WaitNext);
                                _failureState = localmetric.State.CurrentCapacity < 2;
                                diff++;
                            }
                        }
                        else
                        {
                            while (diff > 0 && !_stoptoken.IsCancellationRequested)
                            {
                                if (localmetric.State.CurrentCapacity >= localtarget)
                                {
                                    break;
                                }
                                T buff = default;
                                try
                                {
                                    if (factorySync != null)
                                    {
                                        buff = factorySync.Invoke(_stoptoken);
                                    }
                                    else
                                    {
                                        buff = await factoryAsync.Invoke(_stoptoken);
                                    }
                                    _availableBuffer.Enqueue(buff);
                                }
                                catch (OperationCanceledException)
                                {
                                    continue;
                                }
                                catch (Exception ex)
                                {
                                    if (buff is IDisposable disposable)
                                    {
                                        disposable.Dispose();
                                    }
                                    IncrementError();
                                    if (!blockeventError.IsAddingCompleted)
                                    {
                                        blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException("Factory Error", ex)));
                                    }
                                    if (!item.Item2.HasValue)
                                    {
                                        break;
                                    }
                                }
                                localmetric = CreateMetric(WaitNext);
                                _failureState = localmetric.State.CurrentCapacity < 2;
                                diff--;
                            }
                        }
                        if (!blockeventAutoScaler.IsAddingCompleted)
                        {
                            blockeventAutoScaler.Add(new Tuple<DateTime, RingBufferAutoScaleEventArgs>(DateTime.Now, new RingBufferAutoScaleEventArgs(_alias, oldvalue, localmetric.State.CurrentCapacity, localmetric)));
                        }
                        Interlocked.Exchange(ref _starting, 0);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    WriteLog(DateTime.Now, $"{_alias} RedefineCapacity Thread Stoped");
                }
            });
            _redefineCapacityThread.IsBackground = true;
            _redefineCapacityThread.Start();
        }
        public void UsingAutoScaler(Func<RingBufferMetric, CancellationToken, int> autoscalerSync, Func<RingBufferMetric, CancellationToken, Task<int>> autoscalerAsync, TimeSpan WaitNext, TimeSpan intervalfailure, TimeSpan warmupAutoScaler)
        {
            WriteLog(DateTime.Now, $"{_alias} AutoScaler Buffer Thread Created");
            _autoscalerBufferThread = new Thread(async () =>
            {
                WriteLog(DateTime.Now, $"{_alias} AutoScaler Buffer Thread Running");
                if (warmupAutoScaler.TotalMilliseconds > 0 && !_failureState)
                {
                    WriteLog(DateTime.Now, $"{_alias} Warmup AutoScaler ({warmupAutoScaler.TotalMilliseconds})");
                    if (NaturalTimer.Delay(warmupAutoScaler, _stoptoken))
                    {
                        return;
                    }
                }
                while (!_stoptoken.IsCancellationRequested)
                {
                    if (NaturalTimer.Delay(WaitNext, _stoptoken))
                    {
                        continue;
                    }

                    var localmetric = CreateMetric(WaitNext);
                    _autoScalerCount.ResetCount();
                    var newAvaliable = localmetric.State.CurrentCapacity;
                    if (!localmetric.State.FailureState)
                    {
                        if (localmetric.State.MinimumCapacity == localmetric.State.MaximumCapacity && localmetric.State.CurrentCapacity == localmetric.State.MaximumCapacity)
                        {
                            continue;
                        }
                        try
                        {
                            //user autoscaler
                            if (autoscalerAsync != null)
                            {
                                newAvaliable = await autoscalerAsync.Invoke(localmetric, _stoptoken)
                                        .ConfigureAwait(false);
                            }
                            else if (autoscalerSync != null)
                            {
                                newAvaliable = autoscalerSync.Invoke(localmetric, _stoptoken);
                            }
                            if (newAvaliable < localmetric.State.MinimumCapacity)
                            {
                                newAvaliable = localmetric.State.MinimumCapacity;
                            }
                            if (newAvaliable > localmetric.State.MaximumCapacity)
                            {
                                newAvaliable = localmetric.State.MaximumCapacity;
                            }
                            if (blockAutoScalerBuffer.Count == 0 && !blockAutoScalerBuffer.IsAddingCompleted)
                            {
                                blockAutoScalerBuffer.Add(new Tuple<DateTime, int?>(DateTime.Now, newAvaliable));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }
                        catch (Exception ex)
                        {
                            IncrementError();
                            if (!blockeventError.IsAddingCompleted)
                            {
                                blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException($"{_alias}  Auto Scaler error", ex)));
                            }
                        }
                    }
                    else
                    {
                        NaturalTimer.Delay(intervalfailure, _stoptoken);
                        if (blockAutoScalerBuffer.Count == 0 && !blockAutoScalerBuffer.IsAddingCompleted)
                        {
                            blockAutoScalerBuffer.Add(new Tuple<DateTime, int?>(DateTime.Now, null));
                        }
                    }
                }
                WriteLog(DateTime.Now, $"{_alias} AutoScaler Buffer Thread Stoped");

            });
            _autoscalerBufferThread.IsBackground = true;
            _autoscalerBufferThread.Start();
        }
        public void StartCapacity(int value)
        {
            var localvalue = value;
            if (localvalue > _maxcapacity)
            {
                localvalue = _maxcapacity;
            }
            if (localvalue < _mincapacity)
            {
                localvalue = _mincapacity;
            }
            Interlocked.Exchange(ref _starting, 1);
            if (!blockAutoScalerBuffer.IsAddingCompleted)
            {
                blockAutoScalerBuffer.Add(new Tuple<DateTime, int?>(DateTime.Now, localvalue));
            }
            while (_starting == 1 && !_stoptoken.IsCancellationRequested)
            {
                NaturalTimer.Delay(1);
            }
        }
        public void UsingEventError(EventHandler<RingBufferErrorEventArgs>? errorCallBack)
        {
            if (errorCallBack == null && _logger == null)
            {
                return;
            }
            WriteLog(DateTime.Now, $"{_alias} EventError Thread Created");
            _eventErrorThread = new Thread(() =>
            {
                WriteLog(DateTime.Now, $"{_alias} EventError Thread Running");
                foreach (var item in blockeventError.GetConsumingEnumerable())
                {
                    WriteLog(item.Item1, item.ToString(), LogLevel.Error);
                    errorCallBack?.Invoke(null, new RingBufferErrorEventArgs(_alias, item.Item2));
                }
                WriteLog(DateTime.Now, $"{_alias} EventError Thread Stoped");
            });
            _eventErrorThread.IsBackground = true;
            _eventErrorThread.Start();
        }
        public void UsingEventTimeout(EventHandler<RingBufferTimeoutEventArgs>? timeoutCallBack)
        {
            if (timeoutCallBack == null && _logger == null)
            {
                return;
            }
            WriteLog(DateTime.Now, $"{_alias} EventTimeout Thread Created");
            _eventTimeoutThread = new Thread(() =>
            {
                WriteLog(DateTime.Now, $"{_alias} EventTimeout Thread Running");
                foreach (var item in blockeventTimeout.GetConsumingEnumerable())
                {
                    WriteLog(item.Item1, $"{_alias} Timeout({item.Item2.Timeout}) accquire {item.Item2.ElapsedTime}", LogLevel.Warning);
                    timeoutCallBack?.Invoke(null, item.Item2);
                }
                WriteLog(DateTime.Now, $"{_alias} EventTimeout Thread Stoped");
            });
            _eventTimeoutThread.IsBackground = true;
            _eventTimeoutThread.Start();
        }
        public void UsingEventAutoScaler(EventHandler<RingBufferAutoScaleEventArgs>? autoScaleCallBack)
        {
            if (autoScaleCallBack == null && _logger == null)
            {
                return;
            }
            WriteLog(DateTime.Now, $"{_alias} EventAutoScaler Thread Created");
            _eventAutoScalerThread = new Thread(() =>
            {
                try
                {
                    WriteLog(DateTime.Now, $"{_alias} EventAutoScaler Thread Running");
                    foreach (var item in blockeventAutoScaler.GetConsumingEnumerable(_stoptoken))
                    {
                        WriteLog(item.Item1, $"{_alias} AutoScaler {item.Item2.OldCapacity} to {item.Item2.NewCapacity}. Ava/Run/Err/Tout: {item.Item2.Metric.State.CurrentAvailable}/{item.Item2.Metric.State.CurrentRunning}/{item.Item2.Metric.ErrorCount}/{item.Item2.Metric.TimeoutCount}");
                        autoScaleCallBack?.Invoke(null, item.Item2);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    WriteLog(DateTime.Now, $"{_alias} EventAutoScaler Thread Stoped");
                }
            });
            _eventAutoScalerThread.IsBackground = true;
            _eventAutoScalerThread.Start();
        }
        public void UsingHealthCheck(Func<T, CancellationToken, bool> healthcheckSync, Func<T, CancellationToken, Task<bool>> healthcheckAsync, TimeSpan WaitNext)
        {
            if (healthcheckSync == null && healthcheckAsync == null)
            {
                return;
            }
            WriteLog(DateTime.Now, $"{_alias} HealthCheck Thread Created");
            _healthCheckThread = new Thread(async () =>
            {
                WriteLog(DateTime.Now, $"{_alias} HealthCheck Thread Running");
                while (!_stoptoken.IsCancellationRequested)
                {
                    if (NaturalTimer.Delay(WaitNext, _stoptoken))
                    {
                        continue;
                    }

                    if (healthcheckSync == null && healthcheckAsync == null)
                    {
                        continue;
                    }

                    if (_availableBuffer.TryDequeue(out T tmpBufferElement))
                    {
                        Interlocked.Increment(ref _runningCount);
                        try
                        {
                            bool hc = true;
                            if (healthcheckSync != null)
                            {
                                hc = healthcheckSync(tmpBufferElement, _stoptoken);
                            }
                            else if (healthcheckAsync != null)
                            {
                                hc = await healthcheckAsync(tmpBufferElement, _stoptoken);
                            }
                            if (hc)
                            {
                                lock (_sync)
                                {
                                    _availableBuffer.Enqueue(tmpBufferElement);
                                    Interlocked.Decrement(ref _runningCount);
                                }
                            }
                            else
                            {
                                Interlocked.Decrement(ref _runningCount);
                                if (tmpBufferElement is IDisposable disposable)
                                {
                                    disposable.Dispose();
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Decrement(ref _runningCount);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Decrement(ref _runningCount);
                            IncrementError();
                            if (!blockeventError.IsAddingCompleted)
                            {
                                blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException($"{_alias} HealthCheck Error", ex)));
                            }
                        }
                    }
                }
                WriteLog(DateTime.Now, $"{_alias} HealthCheck Thread Stoped");
            });
            _healthCheckThread.IsBackground = true;
            _healthCheckThread.Start();
        }
        public void UsingReport(Action<RingBufferMetric, CancellationToken> reportSync, Func<RingBufferMetric, CancellationToken, Task> reportAsync, TimeSpan WaitNext)
        {
            if (reportSync == null && reportAsync == null)
            {
                return;
            }
            WriteLog(DateTime.Now, $"{_alias} Report Thread Created");
            _reportBufferThread = new Thread(async () =>
            {
                WriteLog(DateTime.Now, $"{_alias} Report Thread Running");
                while (!_stoptoken.IsCancellationRequested)
                {
                    if (reportSync == null && reportAsync == null)
                    {
                        break;
                    }
                    if (NaturalTimer.Delay(WaitNext, _stoptoken))
                    {
                        continue;
                    }
                    try
                    {
                        var metric = CreateMetricReport(WaitNext);
                        _reportCount.ResetCount();
                        if (reportAsync != null)
                        {
                            await reportAsync(metric, _stoptoken);
                        }
                        else if (reportSync != null)
                        {
                            reportSync(metric, _stoptoken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        IncrementError();
                        if (!blockeventError.IsAddingCompleted)
                        {
                            blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException($"{_alias} Report Error", ex)));
                        }
                    }
                }
                WriteLog(DateTime.Now, $"{_alias} Report Thread Stoped");

            });
            _reportBufferThread.IsBackground = true;
            _reportBufferThread.Start();
        }
        public RingBufferValue<T> ReadBuffer(TimeSpan timeout, TimeSpan waitNextTry, RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null, CancellationToken? usercancellation = null)
        {
            var timer = new NaturalTimer();
            timer.ReStart();
            var localtoken = _stoptoken;
            if (usercancellation.HasValue)
            {
                localtoken = usercancellation.Value;
            }
            T tmpBufferElement;
            if (_failureState)
            {
                Thread.MemoryBarrier();
                _failureState = _availableBuffer.Count + _runningCount < 2;
            }
            if (_failureState)
            {
                timer.Stop();
                NaturalTimer.Delay(1);
                return new RingBufferValue<T>(
                    _alias,
                    CreateState(),
                    (long)timer.TotalMilliseconds,
                    false,
                    new RingBufferException($"{_alias} Failure State", null),
                    default,
                    RenewBuffer);
            }
            timer.ReStart();
            while (!_availableBuffer.TryDequeue(out tmpBufferElement))
            {
                if (localtoken.IsCancellationRequested || _stoptoken.IsCancellationRequested)
                {
                    Exception ex = new RingBufferException($"{_alias} CancellationRequested {nameof(ReadBuffer)}");
                    return new RingBufferValue<T>(
                        _alias,
                        CreateState(),
                        (long)timer.TotalMilliseconds,
                        false,
                        ex,
                        default,
                        RenewBuffer);
                }
                if (NaturalTimer.Delay(waitNextTry, localtoken))
                {
                    Exception ex = new RingBufferException($"{_alias} CancellationRequested {nameof(ReadBuffer)}");
                    return new RingBufferValue<T>(
                        _alias,
                        CreateState(),
                        (long)timer.TotalMilliseconds,
                        false,
                        ex,
                        default,
                        RenewBuffer);
                }
                if (timer.TotalMilliseconds > timeout.TotalMilliseconds)
                {
                    var sta = CreateMetric(timeout);
                    timer.Stop();
                    if (sta.State.FailureState)
                    {
                        return new RingBufferValue<T>(
                            _alias,
                            sta.State,
                            (long)timer.TotalMilliseconds,
                            false,
                            new RingBufferException($"{_alias} Failure State", null),
                            default,
                            RenewBuffer);
                    }
                    else
                    {
                        IncrementTimeout();
                        Exception ex = new RingBufferTimeoutException(nameof(ReadBuffer), (long)timer.TotalMilliseconds, $"{_alias} Timeout({timeout.TotalMilliseconds}) {nameof(ReadBuffer)}({timer.TotalMilliseconds:F0})");
                        if (policy == RingBufferPolicyTimeout.MaximumCapacity && sta.State.CurrentCapacity == _maxcapacity)
                        {
                            if (!blockeventTimeout.IsAddingCompleted)
                            {
                                blockeventTimeout.Add(new Tuple<DateTime, RingBufferTimeoutEventArgs>(DateTime.Now,
                                    new RingBufferTimeoutEventArgs(_alias, (long)timer.TotalMilliseconds, (long)timeout.TotalMilliseconds, CreateState())));
                            }
                        }
                        else if (policy == RingBufferPolicyTimeout.EveryTime)
                        {
                            if (!blockeventTimeout.IsAddingCompleted)
                            {
                                blockeventTimeout.Add(new Tuple<DateTime, RingBufferTimeoutEventArgs>(DateTime.Now,
                                new RingBufferTimeoutEventArgs(_alias, (long)timer.TotalMilliseconds, (long)timeout.TotalMilliseconds, CreateState())));
                            }
                        }
                        else if (policy == RingBufferPolicyTimeout.UserPolicy)
                        {
                            try
                            {
                                var trigger = userpolicy?.Invoke(sta, localtoken) ?? false;
                                if (trigger)
                                {
                                    if (!blockeventTimeout.IsAddingCompleted)
                                    {
                                        blockeventTimeout.Add(new Tuple<DateTime, RingBufferTimeoutEventArgs>(DateTime.Now,
                                        new RingBufferTimeoutEventArgs(_alias, (long)timer.TotalMilliseconds, (long)timeout.TotalMilliseconds, CreateState())));
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                //none
                            }
                            catch (Exception pex)
                            {
                                IncrementError();
                                if (!blockeventError.IsAddingCompleted)
                                {
                                    blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException($"{_alias} User Policy Error", pex)));
                                }
                            }
                        }
                        return new RingBufferValue<T>(
                            _alias,
                            sta.State,
                            (long)timer.TotalMilliseconds,
                            false,
                            ex,
                            default,
                            RenewBuffer);
                    }
                }
                IncrementWaitCount();
            }
            //do not move increment. This is critial order imediate after while loop
            Interlocked.Increment(ref _runningCount);
            timer.Stop();
            IncrementAcquisition();
            return new RingBufferValue<T>(
                _alias,
                CreateState(false),
                (long)timer.TotalMilliseconds,
                true,
                null,
                tmpBufferElement,
                RenewBuffer);
        }
        public RingBufferState CreateState(bool checklinkedFailureState = true)
        {
            var localfailureState = _failureState;
            if (localfailureState)
            {
                Thread.MemoryBarrier();
                _failureState = _availableBuffer.Count + _runningCount < 2;
                localfailureState = _failureState;
            }
            if (!localfailureState && checklinkedFailureState)
            {
                if (_linkedFailureState != null)
                {
                    try
                    {
                        Thread.MemoryBarrier();
                        localfailureState = _linkedFailureState.Invoke();
                        _failureState = localfailureState;
                    }
                    catch (Exception ex)
                    {
                        IncrementError();
                        if (!blockeventError.IsAddingCompleted)
                        {
                            blockeventError.Add(new Tuple<DateTime, Exception>(DateTime.Now, new RingBufferException($"{_alias} Linked FailureState Error", ex)));
                        }
                        localfailureState = true;
                    }
                }
            }
            lock (_sync)
            {
                return new RingBufferState(_runningCount, _availableBuffer.Count, _maxcapacity, _mincapacity, localfailureState);
            }
        }
        public RingBufferMetric CreateMetric(TimeSpan basetime)
        {
            lock (_sync)
            {
                var sta = CreateState();
                return new RingBufferMetric(
                    sta,
                    _alias,
                    _autoScalerCount.TimeoutCount,
                    _autoScalerCount.ErrorCount,
                    _autoScalerCount.WaitCount,
                    _autoScalerCount.AcquisitionCount,
                    _autoScalerCount.AcquisitionSucceeded,
                    _autoScalerCount.AverageSucceeded,
                    basetime);
            }
        }

        internal void WriteLog(DateTime dtref, string message, LogLevel? level = null)
        {
            if (_logger == null)
            {
                return;
            }
            var locallevel = _defaultloglevel;
            if (level.HasValue)
            {
                locallevel = level.Value;
            }
            _logger.Log(locallevel, $"[{dtref}] {message}");
        }
        internal RingBufferMetric CreateMetricReport(TimeSpan basetime)
        {
            lock (_sync)
            {
                var sta = CreateState(false);
                return new RingBufferMetric(
                    sta,
                    _alias,
                    _reportCount.TimeoutCount,
                    _reportCount.ErrorCount,
                    _reportCount.WaitCount,
                    _reportCount.AcquisitionCount,
                    _reportCount.AcquisitionSucceeded,
                    _reportCount.AverageSucceeded,
                    basetime);
            }
        }
        internal void RenewBuffer(RingBufferValue<T> value)
        {
            lock (_sync)
            {
                if (value.SucceededAccquire)
                {
                    IncrementAcquisitionSucceeded(value.ElapsedExecute);
                }
            }
            if (!blockrenewBuffer.IsAddingCompleted)
            {
                blockrenewBuffer.Add(new BackOfficeDisposedBuffer() { Error = value.Error, Buffer = value.Current, SucceededAccquire = value.SucceededAccquire, SkipTurnback = value.SkipTurnback });
            }
        }
        internal void IncrementAcquisitionSucceeded(TimeSpan elapsedExecute)
        {
            _autoScalerCount.IncrementAcquisitionSucceeded(elapsedExecute);
            if (_reportBufferThread != null)
            {
                _reportCount.IncrementAcquisitionSucceeded(elapsedExecute);
            }
        }
        internal void IncrementAcquisition()
        {
            _autoScalerCount.IncrementAcquisition();
            if (_reportBufferThread != null)
            {
                _reportCount.IncrementAcquisition();
            }
        }
        internal void IncrementTimeout()
        {
            _autoScalerCount.IncrementTimeout();
            if (_reportBufferThread != null)
            {
                _reportCount.IncrementTimeout();
            }
        }
        internal void IncrementWaitCount()
        {
            _autoScalerCount.IncrementWaitCount();
            if (_reportBufferThread != null)
            {
                _reportCount.IncrementWaitCount();
            }
        }
        internal void IncrementError()
        {
            _autoScalerCount.IncrementErrorCount();
            if (_reportBufferThread != null)
            {
                _reportCount.IncrementErrorCount();
            }
        }
        private void EndManagerRingBuffer()
        {
            blockAutoScalerBuffer.CompleteAdding();
            if (_autoscalerBufferThread != null)
            {
                _autoscalerBufferThread.Join();
            }
            blockAutoScalerBuffer.Dispose();

            if (_healthCheckThread != null)
            {
                _healthCheckThread.Join();
            }

            if (_reportBufferThread != null)
            {
                _reportBufferThread.Join();
            }

            if (_autoscalerBufferThread != null)
            {
                _autoscalerBufferThread.Join();
            }

            blockrenewBuffer.CompleteAdding();
            if (_renewBufferThread != null)
            {
                _renewBufferThread.Join();
            }
            blockrenewBuffer.Dispose();

            T[] buffer = _availableBuffer.ToArray();
            _availableBuffer.Clear();
            foreach (T item in buffer)
            {
                if (item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            blockeventError.CompleteAdding();
            if (_eventErrorThread != null)
            {
                _eventErrorThread.Join();
            }
            blockeventError.Dispose();

            blockeventTimeout.CompleteAdding();
            if (_eventTimeoutThread != null)
            {
                _eventTimeoutThread.Join();
            }
            blockeventTimeout.Dispose();

            blockeventAutoScaler.CompleteAdding();
            if (_eventAutoScalerThread != null)
            {
                _eventAutoScalerThread.Join();
            }
            blockeventAutoScaler.Dispose();

        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                EndManagerRingBuffer();
                _disposedValue = true;
            }
        }
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
