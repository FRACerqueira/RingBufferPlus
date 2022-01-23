using RingBufferPlus.Events;
using RingBufferPlus.Exceptions;
using RingBufferPlus.Features;
using RingBufferPlus.Internals;
using RingBufferPlus.ObjectValues;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus
{
    public class RingBuffer<T> : IPropertiesRingBuffer, IRingBuffer<T>, IBuildRingBuffer<T>, IRunningRingBuffer<T>, IDisposable
    {

        #region Private Properties

        private struct RingAccquireresult
        {
            public RingAccquireresult()
            {
                ElapsedTime = 0;
                Value = default;
                Error = new InvalidOperationException($"Invalid Create {nameof(RingAccquireresult)}");
            }

            public RingAccquireresult(long elapsedTime, T value, Exception error)
            {
                ElapsedTime = elapsedTime;
                Value = value;
                Error = error;
            }

            public long ElapsedTime { get; }
            public Exception Error { get; }
            public T Value { get; }

        }
        private struct FactoryFunc
        {
            public Func<CancellationToken, T>? ItemSync { get; set; }
            public Func<CancellationToken, Task<T>>? ItemAsync { get; set; }
            public bool ExistFuncAsync => ItemAsync != null;
            public bool ExistFuncSync => ItemSync != null;
            public bool ExistFunc => ExistFuncAsync || ExistFuncSync;
        }

        private readonly ConcurrentQueue<T> availableBuffer;
        private int _targetCapacity;

        private ILoggerFactory _loggerFactory = null;
        private ILogger _logger = null;
        private LogLevel _defaultloglevel = LogLevel.None;

        private Action<RingBufferMetric, CancellationToken> _reportSync;
        private Func<RingBufferMetric, CancellationToken, Task> _reportAsync;
        private TimeSpan _intervalReport;
        private ReportFeature? _reportFeatureInst;
        private Task? _reportTask;
        private bool _stopTaskReport;


        private Func<RingBufferMetric, CancellationToken, Task<int>>? _autoScaleFuncAsync;
        private Func<RingBufferMetric, CancellationToken, int>? _autoScaleFuncSync;
        private TimeSpan _intervalAutoScaler;
        private AutoScalerFeature _autoScalerFeatureInst;
        private Task? _autoScalerTask;
        private bool _stopTaskAutoScaler;
        private bool _triggerTaskAutoScaler;
        private bool _userAutoScaler;


        private Func<T, CancellationToken, Task<bool>> _healthCheckFuncAsync;
        private Func<T, CancellationToken, bool> _healthCheckFuncSync;
        private TimeSpan _intervalHealthCheck;
        private Task? _healthCheckTask;
        private bool _stopTaskHealthCheck;

        private FactoryFunc itemFactoryFunc;

        private RingBufferPolicyTimeout _policytimeoutAccquire;
        private Func<RingBufferMetric, CancellationToken, bool>? _userpolicytimeoutAccquireFunc;
        private Func<RingBufferMetric, CancellationToken, Task<bool>>? _userpolicytimeoutAccquireAsyncFunc;

        private long _runningCount;
        private int CountSkipDelay;

        private CancellationTokenSource _cts;
        private bool _disposedValue;


        #endregion  

        #region Constructor

        public static IRingBuffer<T> CreateRingBuffer(int value)
        {
            return new RingBuffer<T>(value);
        }

        internal RingBuffer(int value)
        {
            if (value <= 0) throw new RingBufferFatalException("RingBufferPlus","InitialCapacity must be greater than zero");

            availableBuffer = new ConcurrentQueue<T>();

            TimeoutAccquire = TimeSpan.Zero;
            WaitNextTry = TimeSpan.Zero;

            _policytimeoutAccquire = RingBufferPolicyTimeout.MaximumCapacity;
            _intervalAutoScaler = TimeSpan.Zero;
            _intervalHealthCheck = TimeSpan.Zero;
            _intervalReport = TimeSpan.Zero;

            InitialCapacity = value;
            MinimumCapacity = -1;
            MaximumCapacity = -1;
            itemFactoryFunc = new FactoryFunc();

        }

        #endregion

        #region IPropertiesRingBuffer

        public RingBufferPolicyTimeout PolicyTimeout => _policytimeoutAccquire;
        public int CurrentRunning => (int)Interlocked.Read(ref _runningCount);
        public int CurrentAvailable => availableBuffer.Count;
        public int CurrentCapacity => CurrentAvailable + CurrentRunning;
        public TimeSpan IntervalHealthCheck => _intervalHealthCheck;
        public TimeSpan IntervalAutoScaler => _intervalAutoScaler;
        public bool HasUserpolicyAccquire => _userpolicytimeoutAccquireFunc != null || _userpolicytimeoutAccquireAsyncFunc != null;
        public bool HasUserHealthCheck => _healthCheckFuncSync != null || _healthCheckFuncAsync != null;
        public bool HasUserAutoScaler => _userAutoScaler;
        public bool HasReport => _reportSync != null || _reportAsync != null;
        public TimeSpan IntervalReport => _intervalReport;
        public LogLevel DefaultLogLevel => _defaultloglevel;
        public bool HasLogging => _loggerFactory != null; 
        public string Alias { get; private set; }
        public int InitialCapacity { get; private set; }
        public int MinimumCapacity { get; private set; }
        public int MaximumCapacity { get; private set; }
        public TimeSpan TimeoutAccquire { get; private set; }
        public TimeSpan WaitNextTry { get; private set; }

        #endregion

        #region IBuildRingBuffer

        public event EventHandler<RingBufferErrorEventArgs>? ErrorCallBack;
        public event EventHandler<RingBufferAutoScaleEventArgs>? AutoScaleCallback;
        public event EventHandler<RingBufferTimeoutEventArgs>? TimeoutCallBack;

        public IRunningRingBuffer<T> Run(CancellationToken? cancellationToken = null)
        {

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);
            _runningCount = 0;

            LogRingBuffer($"{Alias} Start TryInitCapacityMinimum {InitialCapacity}");
            var initresult = TryInitCapacityMinimum();
            LogRingBuffer($"{Alias} End TryInitCapacityMinimum {CurrentCapacity}");

            if (!initresult.Value)
            {
                var err = new RingBufferFatalException("TryInitCapacityMinimum", "Init Capacity falured", initresult.Error);
                LogRingBuffer($"{Alias}Init Capacity falured. {err}", LogLevel.Error);
                throw initresult.Error;
            }
            _targetCapacity = CurrentCapacity;
            if (_cts.IsCancellationRequested)
            {
                return this;
            }

            _autoScalerFeatureInst = new AutoScalerFeature(MaximumCapacity, MinimumCapacity, _intervalAutoScaler, _autoScaleFuncAsync, _autoScaleFuncSync, _cts.Token);
            _autoScalerTask = new Task(async () =>
            {
                await RingAutoScaler(_cts.Token)
                    .ConfigureAwait(false);
                LogRingBuffer($"{Alias} End AutoScaler background");
            }, TaskCreationOptions.LongRunning);

            LogRingBuffer($"{Alias} Start AutoScaler background interval : {_intervalAutoScaler}");
            _autoScalerTask.Start();

            if (_reportSync is not null || _reportAsync is not null)
            {
                _reportFeatureInst = new ReportFeature(MaximumCapacity, MinimumCapacity, _intervalReport, _cts.Token);
                _reportTask = new Task(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (NaturalTimer.Delay(_intervalReport, () => _stopTaskReport, _cts.Token))
                        {
                            if (_stopTaskReport)
                            {
                                break;
                            }
                            continue;
                        }
                        try
                        {
                            if (_reportSync is not null)
                            {
                                _reportSync(CreateMetricReport(), _cts.Token);
                            }
                            if (_reportAsync is not null)
                            {
                                await _reportAsync(CreateMetricReport(), _cts.Token)
                                    .ConfigureAwait(false);
                            }
                        }
                        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException tex)
                        {
                            if (!tex.CancellationToken.IsCancellationRequested)
                            {
                                IncrementError();
                                var err = new RingBufferReportException(Alias, "RingBuffer Report exception.", ex);
                                ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                            }
                        }
                        catch (TaskCanceledException tex) when (tex.CancellationToken.IsCancellationRequested)
                        {
                            continue;
                        }
                        catch (Exception ex)
                        {
                            IncrementError();
                            var err = new RingBufferReportException(Alias, "RingBuffer Report exception.", ex);
                            ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                        }
                    }
                    LogRingBuffer($"{Alias} End Report background");
                }
                , TaskCreationOptions.LongRunning);
                LogRingBuffer($"{Alias} Start Report background interval : {_intervalReport}");
                _reportTask.Start();
            }

            if (_healthCheckFuncSync is not null || _healthCheckFuncAsync is not null)
            {
                _healthCheckTask = new Task(async () =>
                {
                    await RingHealthCheckAsync()
                    .ConfigureAwait(false);
                }
                , TaskCreationOptions.LongRunning);

                LogRingBuffer($"{Alias} Start Health Check background interval : {_intervalHealthCheck}");

                _healthCheckTask.Start();
            }

            return this;
        }

        #endregion

        #region IRunningRingBuffer

        public async Task<RingBufferValue<T>> AccquireAsync(TimeSpan? timeout = null)
        {
            var aux = await RingAccquireAsync(true, false, timeout)
                .ConfigureAwait(false);

            if (aux.Error is RingBufferTimeoutException exception)
            {
                await ApplyPolicyTimeoutAccquireAsync(nameof(AccquireAsync), TimeoutAccquire, exception, CreateMetricAutoScaler())
                    .ConfigureAwait(false);
            }
            return aux;
        }

        public RingBufferValue<T> Accquire(TimeSpan? timeout = null)
        {
            var aux = RingAccquire(true, false, timeout);
            if (aux.Error is RingBufferTimeoutException exception)
            {
                ApplyPolicyTimeoutAccquireAsync(nameof(AccquireAsync), TimeoutAccquire, exception, CreateMetricAutoScaler())
                    .GetAwaiter()
                    .GetResult();
            }
            return aux;
        }


        #endregion

        #region IRingBuffer

        public IBuildRingBuffer<T> Build()
        {
            var defalias = false;
            if (string.IsNullOrEmpty(Alias))
            {
                Alias = $"RingBuffer.{typeof(T).Name}";
                defalias = true;
            }

            if (_loggerFactory != null)
            {
                _logger = _loggerFactory.CreateLogger(Alias);
            }

            if (defalias)
            {
                defalias = true;
                LogRingBuffer($"Create default Alias : {Alias}");
            }
            else 
            {
                LogRingBuffer($"User Alias : {Alias}");
            }

            if (!itemFactoryFunc.ExistFunc)
            {
                var err = new RingBufferFatalException(nameof(itemFactoryFunc), "Factory can't be null");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (InitialCapacity < 1)
            { 
                var err = new RingBufferFatalException(nameof(InitialCapacity), "Initial Capacity must be greater than zero");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (TimeoutAccquire.TotalMilliseconds == 0)
            {
                TimeoutAccquire = DefaultValues.TimeoutAccquire;
                LogRingBuffer($"{Alias} using default TimeoutAccquire {TimeoutAccquire}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user TimeoutAccquire {TimeoutAccquire}");
            }

            if (MinimumCapacity < 0)
            {
                if (MinimumCapacity < 0)
                {
                    MinimumCapacity = InitialCapacity;
                    LogRingBuffer($"{Alias} using default MinimumCapacity {MinimumCapacity}");
                }
            }
            if (MaximumCapacity < 0)
            {
                if (MaximumCapacity < 0)
                {
                    MaximumCapacity = InitialCapacity;
                    LogRingBuffer($"{Alias} using default MaximumCapacity {MinimumCapacity}");
                }
            }

            if (MinimumCapacity > InitialCapacity)
            {
                var err = new RingBufferFatalException(nameof(MinimumCapacity), "MinimumCapacity must be less than  or equal to initial Capacity");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (MaximumCapacity < InitialCapacity)
            {
                var err = new RingBufferFatalException(nameof(MaximumCapacity), "MaximumCapacity must be greater  or equal to than initial Capacity");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (_healthCheckFuncAsync != null || _healthCheckFuncSync != null)
            {
                InitialCapacity++;
                MinimumCapacity++;
                MaximumCapacity++;
                LogRingBuffer($"{Alias} Addind 1 to all capacity because has Health Check");

            }

            LogRingBuffer($"{Alias} InitialCapacity {InitialCapacity}");
            LogRingBuffer($"{Alias} MinimumCapacity {MinimumCapacity}");
            LogRingBuffer($"{Alias} MaximumCapacity {MaximumCapacity}");

            _userAutoScaler = _autoScaleFuncSync != null || _autoScaleFuncAsync != null;
            if (!_userAutoScaler)
            {
                _autoScaleFuncSync = (_, _) => InitialCapacity;
                LogRingBuffer($"{Alias} using Fixed AutoScaler {InitialCapacity}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user AutoScaler");
            }


            if (WaitNextTry.TotalMilliseconds == 0)
            {
                WaitNextTry = DefaultValues.WaitTimeAvailable;
                LogRingBuffer($"{Alias} using default WaitNextTry {TimeoutAccquire}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user WaitNextTry {TimeoutAccquire}");
            }


            if (_intervalReport.TotalMilliseconds == 0)
            {
                _intervalReport = DefaultValues.IntervalReport;
                LogRingBuffer($"{Alias} using default interval Report {_intervalReport}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user interval Report {_intervalReport}");
            }


            if (_intervalHealthCheck.TotalMilliseconds == 0)
            {
                _intervalHealthCheck = DefaultValues.IntervalHealthcheck;
                LogRingBuffer($"{Alias} using default interval Health Check {_intervalHealthCheck}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user interval Health Check {_intervalHealthCheck}");
            }

            if (_intervalAutoScaler.TotalMilliseconds == 0)
            {
                _intervalAutoScaler = DefaultValues.IntervalScaler;
                LogRingBuffer($"{Alias} using default interval Auto Scaler {_intervalAutoScaler}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user interval Auto Scaler {_intervalAutoScaler}");
            }

            return this;
        }

        public IRingBuffer<T> AddLogProvider(RingBufferLogLevel defaultlevel, ILoggerFactory value)
        {
            _defaultloglevel = (LogLevel)Enum.Parse(typeof(LogLevel), defaultlevel.ToString(), true);
            _loggerFactory = value;
            return this;
        }

        public IRingBuffer<T> DefaultTimeoutAccquire(long mileseconds)
        {
            return DefaultTimeoutAccquire(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultTimeoutAccquire(TimeSpan value)
        {
            if (value.TotalMilliseconds == 0) throw new RingBufferFatalException("DefaultTimeoutAccquire", "Timeout Available must be greater than zero");
            TimeoutAccquire = value;
            return this;
        }

        public IRingBuffer<T> AliasName(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new RingBufferFatalException("AliasName","Alias can't be null");
            Alias = value;
            return this;
        }

        public IRingBuffer<T> MaxScaler(int value)
        {
            if (value <= 0) throw new RingBufferFatalException("MaxScaler","MaxAvaliable must be greater than zero");
            MaximumCapacity = value;
            return this;
        }

        public IRingBuffer<T> MinScaler(int value)
        {
            if (value <= 0) throw new RingBufferFatalException("MinScaler","MinAvaliable must be greater than zero");
            MinimumCapacity = value;
            return this;
        }

        public IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value)
        {
            if (value is null) throw new RingBufferFatalException("FactoryAsync","Factory can't be null");
            itemFactoryFunc.ItemAsync = value;
            itemFactoryFunc.ItemSync = null;
            return this;
        }

        public IRingBuffer<T> Factory(Func<CancellationToken, T> value)
        {
            if (value is null) throw new RingBufferFatalException("Factory","Factory can't be null");
            itemFactoryFunc.ItemAsync = null;
            itemFactoryFunc.ItemSync = value;
            return this;
        }

        public IRingBuffer<T> DefaultIntervalHealthCheck(long mileseconds)
        {
            return DefaultIntervalHealthCheck(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalHealthCheck(TimeSpan value)
        {
            if (value.TotalMilliseconds == 0) throw new RingBufferFatalException("DefaultIntervalHealthCheck","Timeout HealthCheck must be greater than zero");
            _intervalHealthCheck = value;
            return this;
        }

        public IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value)
        {
            if (value is null) throw new RingBufferFatalException("HealthCheckAsync", "HealthCheck can't be null");
            _healthCheckFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value)
        {
            if (value is null) throw new RingBufferFatalException("HealthCheck", "HealthCheck can't be null");
            _healthCheckFuncSync = value;
            return this;
        }

        public IRingBuffer<T> DefaultIntervalAutoScaler(long mileseconds)
        {
            return DefaultIntervalAutoScaler(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalAutoScaler(TimeSpan value)
        {
            if (value.TotalMilliseconds == 0) throw new RingBufferFatalException("DefaultIntervalAutoScaler", "Interval AutoScaler must be greater than zero");
            _intervalAutoScaler = value;
            return this;
        }

        public IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> value)
        {
            if (value is null) throw new RingBufferFatalException("AutoScalerAsync", "AutoScaler can't be null");
            _autoScaleFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> value)
        {
            if (value is null) throw new RingBufferFatalException("AutoScaler", "AutoScaler can't be null");
            _autoScaleFuncSync = value;
            return this;
        }

        public IRingBuffer<T> PolicyTimeoutAccquire(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
        {
            if (policy == RingBufferPolicyTimeout.UserPolicy)
            {
                if (userpolicy is null) throw new RingBufferFatalException("PolicyTimeoutAccquire", "User Policy can't be null");
            }
            else
            {
                if (userpolicy is not null) throw new RingBufferFatalException("PolicyTimeoutAccquire", "User Policy must be null");
            }
            _policytimeoutAccquire = policy;
            _userpolicytimeoutAccquireFunc = userpolicy;
            return this;
        }

        public IRingBuffer<T> PolicyTimeoutAccquireAsync(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, Task<bool>>? userpolicy = null)
        {
            if (policy == RingBufferPolicyTimeout.UserPolicy)
            {
                if (userpolicy is null) throw new RingBufferFatalException("PolicyTimeoutAccquireAsync", "User Policy can't be null");
            }
            else
            {
                if (userpolicy is not null) throw new RingBufferFatalException("PolicyTimeoutAccquireAsync", "User Policy must be null");
            }
            _policytimeoutAccquire = policy;
            _userpolicytimeoutAccquireAsyncFunc = userpolicy;
            return this;
        }

        public IRingBuffer<T> DefaultIntervalReport(long mileseconds)
        {
            return DefaultIntervalReport(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalReport(TimeSpan value)
        {
            if (value.TotalMilliseconds == 0) throw new RingBufferFatalException("DefaultIntervalReport", "Interval Report must be greater than zero");
            _intervalReport = value;
            return this;
        }

        public IRingBuffer<T> ReportMetrics(Action<RingBufferMetric, CancellationToken> report)
        {
            if (report is null) throw new RingBufferFatalException("ReportMetrics", "Action Report can't be null");
            _reportSync = report;
            return this;
        }

        public IRingBuffer<T> ReportMetricsAsync(Func<RingBufferMetric, CancellationToken, Task> report)
        {
            if (report is null) throw new RingBufferFatalException("ReportMetricsAsync", "Action Report can't be null");
            _reportAsync = report;
            return this;
        }

        #endregion

        #region Internal Methods (for test)

        internal void TestWaitStopHealthCheck()
        {
            if (_healthCheckTask != null && !_stopTaskHealthCheck)
            {
                _stopTaskHealthCheck = true;
                if (RingBuffer<T>.IsValidStatusTask(_healthCheckTask))
                {
                    _healthCheckTask.Wait();
                }
            }
        }

        internal void TestWaitStopReport()
        {
            if (_reportTask != null && !_stopTaskReport)
            {
                _stopTaskReport = true;
                if (RingBuffer<T>.IsValidStatusTask(_reportTask))
                {
                    _reportTask.Wait();
                }
            }
        }

        internal void TestWaitStopAutoScaler()
        {
            if (_autoScalerTask != null && !_stopTaskAutoScaler)
            {
                _stopTaskAutoScaler = true;
                if (RingBuffer<T>.IsValidStatusTask(_autoScalerTask))
                {
                    _autoScalerTask.Wait();
                }
            }
        }

        internal void TestWaitStopAllTasks()
        {
            TestWaitStopAutoScaler();
            TestWaitStopHealthCheck();
            TestWaitStopReport();
        }

        internal void TestTriggerAutoScale()
        {
            _triggerTaskAutoScaler = true;
        }

        internal RingBufferMetric TestMetricAutoScaler()
        {
            return CreateMetricAutoScaler();
        }


        #endregion

        #region Private Methods

        private void LogRingBuffer(string message, LogLevel? level = null)
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
            var msg = $"[{DateTime.Now}] {message}";
            _logger.Log(locallevel, msg);
        }

        private static bool IsValidStatusTask(Task task)
        {
            return task.Status switch
            {
                TaskStatus.Created => false,
                TaskStatus.WaitingForActivation => true,
                TaskStatus.WaitingToRun => true,
                TaskStatus.Running => true,
                TaskStatus.WaitingForChildrenToComplete => true,
                TaskStatus.RanToCompletion => false,
                TaskStatus.Canceled => false,
                TaskStatus.Faulted => false,
                _ => false,
            };
        }

        private void RefreshContextBuffer(T value)
        {
            availableBuffer.Enqueue(value);
            Interlocked.Decrement(ref _runningCount);
        }

        private async Task RingHealthCheckAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                _ = NaturalTimer.Delay(_intervalHealthCheck, () => _stopTaskHealthCheck, _cts.Token);
                if (_stopTaskHealthCheck)
                {
                    break;
                }
                if (availableBuffer.IsEmpty)
                {
                    continue;
                }

                var accq = false;
                using (var tmpBufferElement = RingAccquire(false, true, TimeoutAccquire))
                {
                    accq = tmpBufferElement.SucceededAccquire;
                    if (accq)
                    {
                        try
                        {
                            bool hc;
                            if (_healthCheckFuncAsync is not null)
                            {
                                hc = await _healthCheckFuncAsync(tmpBufferElement.Current, _cts.Token)
                                            .ConfigureAwait(false);
                            }
                            else
                            {
                                hc = _healthCheckFuncSync(tmpBufferElement.Current, _cts.Token);
                            }
                            if (hc)
                            {
                                RefreshContextBuffer(tmpBufferElement.Current);
                            }
                        }
                        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException tex)
                        {
                            if (!tex.CancellationToken.IsCancellationRequested)
                            {
                                IncrementError();
                                var err = new RingBufferHealthCheckException(Alias, "RingBuffer Health Check exception.", ex);
                                LogRingBuffer($"{Alias} trigger Health Check Error: {err}", LogLevel.Error);
                                ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                            }
                        }
                        catch (TaskCanceledException tex) when (tex.CancellationToken.IsCancellationRequested)
                        {
                            continue;
                        }
                        catch (Exception ex)
                        {
                            IncrementError();
                            var err = new RingBufferHealthCheckException(Alias, "RingBuffer Health Check exception.", ex);
                            LogRingBuffer($"{Alias} trigger Health Check Error: {err}", LogLevel.Error);
                            ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                        }
                    }
                    else
                    {
                        if (tmpBufferElement.Error != null)
                        {
                            if (tmpBufferElement.Error is RingBufferTimeoutException exception)
                            {
                                continue;
                            }
                            else
                            {
                                IncrementError();
                                var err = new RingBufferAccquireException(Alias, "RingBuffer Accquire(Health Check) exception.", tmpBufferElement.Error);
                                LogRingBuffer($"{Alias} trigger Health Error: {err}", LogLevel.Error);
                                ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                            }
                        }
                    }
                }
                if (accq)
                {
                    DecrementAcquisition();
                }
            }
            LogRingBuffer($"{Alias} End Health Check background");
        }

        private async Task ApplyPolicyTimeoutAccquireAsync(string source, TimeSpan basetime, RingBufferTimeoutException exception, RingBufferMetric metric)
        {

            if (_policytimeoutAccquire == RingBufferPolicyTimeout.MaximumCapacity)
            {
                if (metric.Capacity >= metric.Maximum && metric.Avaliable == 0)
                {
                    LogRingBuffer($"{Alias} trigger policy(MaximumCapacity) Timeout accquire : {exception.ElapsedTime}/{(long)basetime.TotalMilliseconds}");
                    TimeoutCallBack?.Invoke(this, new RingBufferTimeoutEventArgs(
                        Alias,
                        nameof(source),
                        exception.ElapsedTime,
                        (long)basetime.TotalMilliseconds,
                        metric));
                }
            }
            else if (_policytimeoutAccquire == RingBufferPolicyTimeout.EveryTime)
            {
                LogRingBuffer($"{Alias} trigger policy(EveryTime) Timeout accquire : {exception.ElapsedTime}/{(long)basetime.TotalMilliseconds}");
                TimeoutCallBack?.Invoke(this, new RingBufferTimeoutEventArgs(
                    Alias,
                    nameof(source),
                    exception.ElapsedTime,
                    (long)basetime.TotalMilliseconds,
                    metric));
            }
            else if (_policytimeoutAccquire == RingBufferPolicyTimeout.UserPolicy)
            {
                var tmout = new ValueException<bool>(false, null);
                try
                {
                    if (_userpolicytimeoutAccquireFunc != null)
                    {
                        tmout = new ValueException<bool>(_userpolicytimeoutAccquireFunc(metric, _cts.Token), null);
                    }
                    else if (_userpolicytimeoutAccquireAsyncFunc != null)
                    {
                        tmout = new ValueException<bool>(await _userpolicytimeoutAccquireAsyncFunc(metric, _cts.Token)
                                        .ConfigureAwait(false), null);
                    }
                }
                catch (Exception ex)
                {
                    tmout = new ValueException<bool>(false, ex);
                }
                if (tmout.Value || tmout.Error != null)
                {
                    if (tmout.Error != null)
                    {
                        var err = new RingBufferPolicyTimeoutAccquireException(Alias, exception.Message, new Exception[] { exception, tmout.Error });
                        LogRingBuffer($"{Alias} trigger Error Policy(UserPolicy) : {err}", LogLevel.Error);
                        ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                    }
                    else
                    {
                        LogRingBuffer($"{Alias} trigger policy(EveryTime) Timeout accquire : {exception.ElapsedTime}/{(long)basetime.TotalMilliseconds}", LogLevel.Warning);
                        TimeoutCallBack?.Invoke(this, new RingBufferTimeoutEventArgs(
                            Alias,
                            nameof(source),
                            exception.ElapsedTime,
                            (long)basetime.TotalMilliseconds,
                            metric));
                    }
                }
            }
            if (_policytimeoutAccquire == RingBufferPolicyTimeout.Ignore)
            {
                //none
            }
        }

        private RingBufferValue<T> RingAccquire(bool restorebuffer, bool skiphc, TimeSpan? timeout = null)
        {
            var localtmout = TimeoutAccquire;
            if (timeout.HasValue)
            {
                localtmout = timeout.Value;
            }

            Action<T> turnback = restorebuffer ? RefreshContextBuffer : null;

            var result = CommonRingAccquire(nameof(RingAccquire), localtmout);

            if (CountSkipDelay > 1)
            {
                _ = NaturalTimer.Delay(TimeSpan.FromMilliseconds(1), null, _cts.Token);
                Interlocked.Exchange(ref CountSkipDelay, 0);
            }
            Interlocked.Increment(ref CountSkipDelay);

            bool hc = true;
            if (result.Error == null)
            {
                if (skiphc)
                {
                    return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, true, null, result.Value, turnback);
                }
                try
                {
                    if (_healthCheckFuncSync != null)
                    {
                        hc = _healthCheckFuncSync.Invoke(result.Value, _cts.Token);
                    }
                    else if (_healthCheckFuncAsync != null)
                    {
                        hc = _healthCheckFuncAsync.Invoke(result.Value, _cts.Token)
                                    .GetAwaiter()
                                    .GetResult();
                    }
                }
                catch (Exception ex)
                {
                    var err = new InvalidOperationException("Health Check Exception", ex);
                    return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, err, result.Value, turnback);
                }
            }
            else
            {
                return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, result.Error, result.Value, turnback);
            }
            if (!hc)
            {
                IncrementError();
                return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, null, result.Value, turnback);
            }
            return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, true, null, result.Value, turnback);
        }

        private async Task<RingBufferValue<T>> RingAccquireAsync(bool restorebuffer, bool skiphc, TimeSpan? timeout = null)
        {
            var localtmout = TimeoutAccquire;
            if (timeout.HasValue)
            {
                localtmout = timeout.Value;
            }

            Action<T> turnback = restorebuffer ? RefreshContextBuffer : null;

            var result = CommonRingAccquire(nameof(RingAccquireAsync), localtmout);

            if (CountSkipDelay > 1)
            {
                _ = NaturalTimer.Delay(TimeSpan.FromMilliseconds(1), null, _cts.Token);
                Interlocked.Exchange(ref CountSkipDelay, 0);
            }
            Interlocked.Increment(ref CountSkipDelay);

            bool hc = true;
            if (result.Error == null)
            {
                if (skiphc)
                {
                    return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, true, null, result.Value, turnback);
                }
                try
                {
                    if (_healthCheckFuncSync != null)
                    {
                        hc = _healthCheckFuncSync.Invoke(result.Value, _cts.Token);
                    }
                    else if (_healthCheckFuncAsync != null)
                    {
                        hc = await _healthCheckFuncAsync.Invoke(result.Value, _cts.Token)
                                    .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    var err = new InvalidOperationException("Health Check Exception", ex);
                    return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, err, result.Value, turnback);
                }
            }
            else
            {
                return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, result.Error, result.Value, turnback);
            }

            if (!hc)
            {
                IncrementError();
                return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, false, null, result.Value, turnback);
            }
            return new RingBufferValue<T>(Alias, CurrentAvailable, result.ElapsedTime, true, null, result.Value, turnback);
        }

        private RingAccquireresult CommonRingAccquire(string source, TimeSpan timeout)
        {
            if (CurrentCapacity == 0)
            {
                IncrementError();
                var err = new InvalidOperationException($"{Alias} with capacity unhealthy! Minumum {MinimumCapacity} , Current is zero.");
                _ = NaturalTimer.Delay(WaitNextTry, null, _cts.Token);
                return new RingAccquireresult(0, default, err);
            }


            var timer = new NaturalTimer();
            timer.Start();
            var avaliable = availableBuffer.TryDequeue(out T tmpBufferElement);
            while (!avaliable)
            {
                IncrementWaitCount();

                if (timer.TotalMilliseconds > timeout.TotalMilliseconds)
                {
                    timer.Stop();
                    IncrementTimeout();
                    var err = new RingBufferTimeoutException(source, (long)timer.TotalMilliseconds, $"Timeout({timeout.TotalMilliseconds}) {source}({timer.TotalMilliseconds:F0})");
                    return new RingAccquireresult((long)timer.TotalMilliseconds, default, err);
                }
                if (NaturalTimer.Delay(WaitNextTry, null, _cts.Token))
                {
                    timer.Stop();
                    var err = new TaskCanceledException("CancellationRequested", null, _cts.Token);
                    return new RingAccquireresult((long)timer.TotalMilliseconds, default, err);
                }
                avaliable = availableBuffer.TryDequeue(out tmpBufferElement);
            }
            timer.Stop();

            IncrementAcquisition();

            return new RingAccquireresult((long)timer.TotalMilliseconds, tmpBufferElement, null);
        }

        private RingBufferMetric CreateMetricReport()
        {
            return new(
                Alias,
                _targetCapacity,
                _reportFeatureInst.TimeoutCount,
                _reportFeatureInst.ErrorCount,
                _reportFeatureInst.WaitCount,
                _reportFeatureInst.AcquisitionCount,
                CurrentRunning,
                MinimumCapacity,
                MaximumCapacity,
                CurrentAvailable,
                _intervalReport);
        }

        private RingBufferMetric CreateMetricAutoScaler()
        {
            return new(
                Alias,
                _targetCapacity,
                _autoScalerFeatureInst.TimeoutCount,
                _autoScalerFeatureInst.ErrorCount,
                _autoScalerFeatureInst.WaitCount,
                _autoScalerFeatureInst.AcquisitionCount,
                CurrentRunning,
                MinimumCapacity,
                MaximumCapacity,
                CurrentAvailable,
                _intervalAutoScaler);
        }

        private async ValueTask RingAutoScaler(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                //wait interval to next autoscaler
                if (NaturalTimer.Delay(_intervalAutoScaler, () => _stopTaskAutoScaler, cancellationToken))
                {
                    return;
                }
                var localmetric = CreateMetricAutoScaler();

                //autoscaler
                int newAvaliable = await _autoScalerFeatureInst
                    .ExecuteAync(localmetric)
                    .ConfigureAwait(false);

                if (newAvaliable < MinimumCapacity)
                {
                    newAvaliable = MinimumCapacity;
                }
                if (newAvaliable > MaximumCapacity)
                {
                    newAvaliable = MaximumCapacity;
                }

                _targetCapacity = RedefineCapacity(newAvaliable, localmetric.Capacity);


                if (localmetric.Capacity != _targetCapacity || _triggerTaskAutoScaler)
                {
                    _triggerTaskAutoScaler = false;
                    LogRingBuffer($"{Alias} trigger AutoScale: {localmetric.Capacity} to {_targetCapacity}");
                    AutoScaleCallback?.Invoke(this, new RingBufferAutoScaleEventArgs(Alias, localmetric.Capacity, _targetCapacity, localmetric));
                }
            }
        }

        private int RedefineCapacity(int target, int current)
        {
            var diff = target - current;
            var countdiff = 0;
            if (diff == 0)
            {
                return current;
            }
            else if (diff < 0)
            {
                while (diff < 0)
                {
                    if (CurrentCapacity <= MinimumCapacity)
                    {
                        break;
                    }
                    if (!availableBuffer.TryDequeue(out T deletebuffer))
                    {
                        if (NaturalTimer.Delay(WaitNextTry, () => _stopTaskAutoScaler, _cts.Token))
                        {
                            return current + countdiff;
                        }
                    }
                    else
                    {
                        if (deletebuffer is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        diff++;
                        countdiff--;
                    }
                }
                return current + countdiff;
            }
            Parallel.For(0, diff, async (pos, state) =>
            {
                try
                {
                    if (CurrentCapacity >= MaximumCapacity)
                    {
                        state.Break();
                    }
                    if (itemFactoryFunc.ExistFuncSync)
                    {
                        var buff = itemFactoryFunc.ItemSync(_cts.Token);
                        availableBuffer.Enqueue(buff);
                        countdiff++;
                    }
                    else if (itemFactoryFunc.ExistFuncAsync)
                    {
                        var buff = await itemFactoryFunc.ItemAsync(_cts.Token)
                            .ConfigureAwait(false);
                        availableBuffer.Enqueue(buff);
                        countdiff++;
                    }
                }
                catch (Exception ex)
                {
                    IncrementError();
                    var err = new RingBufferFactoryException(Alias, $"RingBuffer Factory({Alias}) exception", ex);
                    LogRingBuffer($"{Alias} trigger factory exception : {err}", LogLevel.Error);
                    ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, err));
                }
            });
            return current + countdiff;
        }

        private void IncrementTimeout()
        {
            _autoScalerFeatureInst.IncrementTimeout();
            if (_reportFeatureInst is not null)
            {
                _reportFeatureInst.IncrementTimeout();
            }
        }

        private void IncrementError()
        {
            _autoScalerFeatureInst.IncrementErrorCount();
            if (_reportFeatureInst is not null)
            {
                _reportFeatureInst.IncrementErrorCount();
            }
        }

        private void IncrementWaitCount()
        {
            _autoScalerFeatureInst.IncrementWaitCount();
            if (_reportFeatureInst is not null)
            {
                _reportFeatureInst.IncrementWaitCount();
            }
        }

        private void DecrementAcquisition()
        {
            _autoScalerFeatureInst.DecrementAcquisition();
            if (_reportFeatureInst is not null)
            {
                _reportFeatureInst.DecrementAcquisition();
            }
        }

        private void IncrementAcquisition()
        {
            _autoScalerFeatureInst.IncrementAcquisition();
            Interlocked.Increment(ref _runningCount);
            if (_reportFeatureInst is not null)
            {
                _reportFeatureInst.IncrementAcquisition();
            }
        }

        private ValueException<bool> TryInitCapacityMinimum()
        {
            ValueException<bool>? result = null;
            Parallel.For(0, InitialCapacity, async (pos, state) =>
            {
                try
                {
                    if (itemFactoryFunc.ExistFuncSync)
                    {
                        var buff = itemFactoryFunc.ItemSync(_cts.Token);
                        availableBuffer.Enqueue(buff);
                    }
                    else if (itemFactoryFunc.ExistFuncAsync)
                    {
                        var buff = await itemFactoryFunc.ItemAsync(_cts.Token)
                            .ConfigureAwait(false);
                        availableBuffer.Enqueue(buff);
                    }
                }
                catch (Exception ex)
                {
                    if (MinimumCapacity == InitialCapacity)
                    {
                        var err = new RingBufferFactoryException(Alias, $"{Alias} Min({MinimumCapacity}) capacity failed, current {availableBuffer.Count}", ex);
                        result = new ValueException<bool>(false, err);
                        state.Break();
                    }
                }
            });
            if (result.HasValue)
            {
                return result.Value;
            }
            if (availableBuffer.Count < MinimumCapacity)
            {
                var err = new RingBufferFatalException("TryInitCapacityMinimum", $"{Alias} Min({MinimumCapacity}) capacity failed, current {availableBuffer.Count}");
                return new ValueException<bool>(false, err);
            }
            return new ValueException<bool>(true, null);
        }

        #endregion

        #region IDispose

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing && _cts is not null)
                {
                    _cts.Cancel();
                    if (_healthCheckTask != null)
                    {
                        if (IsValidStatusTask(_healthCheckTask))
                        {
                            _healthCheckTask.Wait();
                        }
                        _healthCheckTask.Dispose();
                    }
                    if (_reportTask != null)
                    {
                        if (IsValidStatusTask(_reportTask))
                        {
                            _reportTask.Wait();
                        }
                        _reportTask.Dispose();
                    }
                    if (_autoScalerTask != null)
                    {
                        if (IsValidStatusTask(_autoScalerTask))
                        {
                            _autoScalerTask.Wait();
                        }
                        _autoScalerTask.Dispose();
                    }
                    T[] buffer = availableBuffer.ToArray();
                    availableBuffer.Clear();
                    foreach (T item in buffer)
                    {
                        if (item is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                    _cts.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
