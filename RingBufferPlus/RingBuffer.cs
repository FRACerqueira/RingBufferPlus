using Microsoft.Extensions.Logging;
using RingBufferPlus.Events;
using RingBufferPlus.Exceptions;
using RingBufferPlus.Features;
using RingBufferPlus.Internals;
using RingBufferPlus.ObjectValues;
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
                throw new InvalidOperationException($"Invalid Create {nameof(RingAccquireresult)}");
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

        private readonly ConcurrentQueue<T> availableBuffer;
        private CircuitBreaker<T> _circuitBreaker;

        private ILoggerFactory _loggerFactory = null;
        private ILogger _logger = null;
        private LogLevel _defaultloglevel = LogLevel.None;

        private Action<RingBufferMetric, CancellationToken> _reportSync;
        private Func<RingBufferMetric, CancellationToken, Task> _reportAsync;
        private TimeSpan _intervalReport;
        private ReportCount _reportCount = new ReportCount();
        private Task? _reportTask;
        private bool _stopTaskReport;

        private Func<bool> _linkedFailureStateFunc;

        private Func<RingBufferMetric, CancellationToken, Task<int>>? _autoScaleFuncAsync;
        private Func<RingBufferMetric, CancellationToken, int>? _autoScaleFuncSync;
        private TimeSpan _intervalAutoScaler;
        private AutoScalerCount _autoScalercount = new AutoScalerCount();
        private Task? _autoScalerTask;
        private bool _stopTaskAutoScaler;
        private bool _triggerTaskAutoScaler;
        private bool _userAutoScaler;

        private Func<T, CancellationToken, Task<bool>> _healthCheckFuncAsync;
        private Func<T, CancellationToken, bool> _healthCheckFuncSync;
        private TimeSpan _intervalHealthCheck;
        private Task? _healthCheckTask;
        private bool _stopTaskHealthCheck;

        private FactoryFunc<T> itemFactoryFunc;

        private RingBufferPolicyTimeout _policytimeoutAccquire;
        private Func<RingBufferMetric, CancellationToken, bool>? _userpolicytimeoutAccquireFunc;

        private volatile int _runningCount;

        private int CountSkipDelay;

        private CancellationTokenSource _cts;
        private bool _disposedValue;

        private readonly object _lock = new object();

        private TimeSpan _intervalOpenCircuit;

        #endregion  

        #region Constructor

        public static IRingBuffer<T> CreateBuffer(int value = 2)
        {
            if (value <= 1) throw new RingBufferException("InitialBuffer must be greater than 1");
            return new RingBuffer<T>(value);
        }

        internal RingBuffer(int value)
        {
            availableBuffer = new ConcurrentQueue<T>();
            TimeoutAccquire = TimeSpan.Zero;
            WaitNextTry = TimeSpan.Zero;

            _policytimeoutAccquire = RingBufferPolicyTimeout.MaximumCapacity;
            _intervalAutoScaler = TimeSpan.Zero;
            _intervalHealthCheck = TimeSpan.Zero;
            _intervalReport = TimeSpan.Zero;
            _intervalOpenCircuit = TimeSpan.Zero;
            InitialCapacity = value;
            MinimumCapacity = 2;
            MaximumCapacity = value;
            itemFactoryFunc = new FactoryFunc<T>();

        }

        #endregion

        #region IPropertiesRingBuffer

        public RingBufferState CurrentState
        {
            get
            {
                lock (_lock)
                {
                    var runcap = _runningCount;
                    var avacap = availableBuffer.Count;
                    var hassick = (runcap + avacap) < 2;
                    if (!hassick && _linkedFailureStateFunc != null)
                    {
                        try
                        {
                            hassick = _linkedFailureStateFunc.Invoke();
                        }
                        catch (Exception)
                        {
                            hassick = true;
                        }
                    }
                    return new RingBufferState(runcap, avacap, MaximumCapacity, MinimumCapacity, hassick);
                }
            }
        }

        private RingBufferState InternalCurrentState => new RingBufferState(_runningCount, availableBuffer.Count, MaximumCapacity, MinimumCapacity, false);

        public RingBufferPolicyTimeout PolicyTimeout => _policytimeoutAccquire;

        public TimeSpan IntervalHealthCheck => _intervalHealthCheck;
        public TimeSpan IntervalAutoScaler => _intervalAutoScaler;
        public TimeSpan IntervalReport => _intervalReport;
        public TimeSpan TimeoutAccquire { get; private set; }
        public TimeSpan WaitNextTry { get; private set; }
        public TimeSpan IntervalOpenCircuit => _intervalOpenCircuit;

        public bool HasUserpolicyAccquire => _userpolicytimeoutAccquireFunc != null;
        public bool HasUserHealthCheck => _healthCheckFuncSync != null || _healthCheckFuncAsync != null;
        public bool HasUserAutoScaler => _userAutoScaler;
        public bool HasLinkedFailureState => _linkedFailureStateFunc != null;
        public bool HasReport => _reportSync != null || _reportAsync != null;
        public bool HasLogging => _loggerFactory != null;

        public LogLevel DefaultLogLevel => _defaultloglevel;
        public string Alias { get; private set; }
        public int InitialCapacity { get; private set; }
        public int MinimumCapacity { get; private set; }
        public int MaximumCapacity { get; private set; }

        #endregion

        #region IBuildRingBuffer

        public event EventHandler<RingBufferErrorEventArgs>? ErrorCallBack;
        public event EventHandler<RingBufferAutoScaleEventArgs>? AutoScalerCallback;
        public event EventHandler<RingBufferTimeoutEventArgs>? TimeoutCallBack;

        public IRunningRingBuffer<T> Run(CancellationToken? cancellationToken = null)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);
            _runningCount = 0;

            _circuitBreaker = new CircuitBreaker<T>(itemFactoryFunc, _cts.Token);

            LogRingBuffer($"{Alias} Start TryInitCapacityMinimum {InitialCapacity}");


            var hassick = false;
            if (!hassick && _linkedFailureStateFunc != null)
            {
                try
                {
                    hassick = _linkedFailureStateFunc.Invoke();
                }
                catch (Exception ex)
                {
                    hassick = true;
                    IncrementError("Linked Failure State Error", ex);
                }
            }

            TryAddCapacityAsync(InitialCapacity, _cts.Token)
                .GetAwaiter()
                .GetResult();

            LogRingBuffer($"{Alias} End TryInitCapacityMinimum {InternalCurrentState.CurrentCapacity}");

            if (_cts.IsCancellationRequested)
            {
                return this;
            }

            _autoScalerTask = new Task(async () =>
            {
                await RingAutoScaler();
            }, TaskCreationOptions.LongRunning);

            LogRingBuffer($"{Alias} Start AutoScaler background interval : {_intervalAutoScaler}");
            _autoScalerTask.Start();

            if (_reportSync is not null || _reportAsync is not null)
            {
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
                                await _reportAsync(CreateMetricReport(), _cts.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            //none
                        }
                        catch (Exception ex)
                        {
                            IncrementError("RingBuffer Report exception.", ex);
                        }
                        finally
                        {
                            _reportCount.ResetCount();
                        }
                    }
                    LogRingBuffer($"{Alias} End Report background");
                }, TaskCreationOptions.LongRunning);
                LogRingBuffer($"{Alias} Start Report background interval : {_intervalReport}");
                _reportTask.Start();
            }

            if (_healthCheckFuncSync is not null || _healthCheckFuncAsync is not null)
            {
                _healthCheckTask = new Task(async () =>
                {
                    await RingHealthCheckAsync();
                }, TaskCreationOptions.LongRunning);

                LogRingBuffer($"{Alias} Start Health Check background interval : {_intervalHealthCheck}");

                _healthCheckTask.Start();
            }
            return this;
        }

        #endregion

        #region IRunningRingBuffer

        public RingBufferValue<T> Accquire(TimeSpan? timeout = null)
        {
            var aux = RingAccquire(timeout);
            if (!aux.SucceededAccquire)
            {
                if (!aux.State.FailureState)
                {
                    if (aux.Error is RingBufferTimeoutException exception)
                    {
                        ApplyPolicyTimeoutAccquireAsync(nameof(Accquire), TimeoutAccquire, exception, CreateMetricAutoScaler(aux.State));
                    }
                }
            }
            return aux;
        }


        #endregion

        #region IRingBuffer

        public IRingBuffer<T> LinkedFailureState(Func<bool> value)
        {
            _linkedFailureStateFunc = value ?? throw new RingBufferException("Linked Function can't be null");
            return this;
        }

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
                var err = new RingBufferException("Factory can't be null");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (InitialCapacity <= 1)
            {
                var err = new RingBufferException("Initial Capacity must be greater than 1");
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

            if (_intervalOpenCircuit.TotalMilliseconds == 0)
            {
                _intervalOpenCircuit = DefaultValues.IntervalOpenCircuit;
                LogRingBuffer($"{Alias} using default IntervalOpenCircuit {IntervalOpenCircuit}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user IntervalOpenCircuit {IntervalOpenCircuit}");
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
                var err = new RingBufferException("MinimumCapacity must be less than  or equal to initial Capacity");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
            }

            if (MaximumCapacity < InitialCapacity)
            {
                var err = new RingBufferException("MaximumCapacity must be greater  or equal to than initial Capacity");
                LogRingBuffer($"{Alias} Fatal Error: {err}", LogLevel.Error);
                throw err;
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

        public IRingBuffer<T> DefaultIntervalOpenCircuit(long mileseconds)
        {
            return DefaultIntervalOpenCircuit(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalOpenCircuit(TimeSpan value)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Interval Open Circuit must be greater than zero");
            _intervalOpenCircuit = value;
            return this;
        }

        public IRingBuffer<T> DefaultTimeoutAccquire(long mileseconds)
        {
            return DefaultTimeoutAccquire(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultTimeoutAccquire(TimeSpan value)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Timeout Available must be greater than zero");
            TimeoutAccquire = value;
            return this;
        }

        public IRingBuffer<T> AliasName(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new RingBufferException("Alias can't be null");
            Alias = value;
            return this;
        }

        public IRingBuffer<T> InitialBuffer(int value)
        {
            if (value <= 1) throw new RingBufferException("InitialBuffer must be greater than 1");
            InitialCapacity = value;
            if (MaximumCapacity < InitialCapacity)
            {
                MaximumCapacity = InitialCapacity;
            }
            return this;
        }

        public IRingBuffer<T> MaxBuffer(int value)
        {
            if (value <= 1) throw new RingBufferException("MaxAvaliable must be greater than 1");
            MaximumCapacity = value;
            return this;
        }

        public IRingBuffer<T> MinBuffer(int value)
        {
            if (value <= 1) throw new RingBufferException("MinAvaliable must be greater than 1");
            MinimumCapacity = value;
            return this;
        }

        public IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value)
        {
            if (value is null) throw new RingBufferException("Factory can't be null");
            itemFactoryFunc.ItemAsync = value;
            itemFactoryFunc.ItemSync = null;
            return this;
        }

        public IRingBuffer<T> Factory(Func<CancellationToken, T> value)
        {
            if (value is null) throw new RingBufferException("Factory can't be null");
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
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Timeout HealthCheck must be greater than zero");
            _intervalHealthCheck = value;
            return this;
        }

        public IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value)
        {
            if (value is null) throw new RingBufferException("HealthCheck can't be null");
            _healthCheckFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value)
        {
            if (value is null) throw new RingBufferException("HealthCheck can't be null");
            _healthCheckFuncSync = value;
            return this;
        }

        public IRingBuffer<T> DefaultIntervalAutoScaler(long mileseconds)
        {
            return DefaultIntervalAutoScaler(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalAutoScaler(TimeSpan value)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Interval AutoScaler must be greater than zero");
            _intervalAutoScaler = value;
            return this;
        }

        public IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> value)
        {
            if (value is null) throw new RingBufferException("AutoScaler can't be null");
            _autoScaleFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> value)
        {
            if (value is null) throw new RingBufferException("AutoScaler can't be null");
            _autoScaleFuncSync = value;
            return this;
        }

        public IRingBuffer<T> PolicyTimeoutAccquire(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
        {
            if (policy == RingBufferPolicyTimeout.UserPolicy)
            {
                if (userpolicy is null) throw new RingBufferException("User Policy can't be null");
            }
            else
            {
                if (userpolicy is not null) throw new RingBufferException("User Policy must be null");
            }
            _policytimeoutAccquire = policy;
            _userpolicytimeoutAccquireFunc = userpolicy;
            return this;
        }

        public IRingBuffer<T> DefaultIntervalReport(long mileseconds)
        {
            return DefaultIntervalReport(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> DefaultIntervalReport(TimeSpan value)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Interval Report must be greater than zero");
            _intervalReport = value;
            return this;
        }

        public IRingBuffer<T> MetricsReport(Action<RingBufferMetric, CancellationToken> report)
        {
            if (report is null) throw new RingBufferException("Action Report can't be null");
            _reportSync = report;
            return this;
        }

        public IRingBuffer<T> MetricsReportAsync(Func<RingBufferMetric, CancellationToken, Task> report)
        {
            if (report is null) throw new RingBufferException("Action Report can't be null");
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
                _healthCheckTask.Wait();
            }
        }

        internal void TestWaitStopReport()
        {
            if (_reportTask != null && !_stopTaskReport)
            {
                _stopTaskReport = true;
                _reportTask.Wait();
            }
        }

        internal void TestWaitStopAutoScaler()
        {
            if (_autoScalerTask != null && !_stopTaskAutoScaler)
            {
                _stopTaskAutoScaler = true;
                _autoScalerTask.Wait();
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
            return CreateMetricAutoScaler(CurrentState);
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
            _logger.Log(locallevel, $"[{DateTime.Now}] {message}");
        }


        private void RenewContextBuffer(RingBufferValue<T> value)
        {
            if (_reportTask != null && value.SucceededAccquire)
            {
                _reportCount.IncrementAcquisitionSucceeded();
                _reportCount.AddTotaSucceededlExecution(value.ElapsedExecute);
            }
            if (!value.SkiptTurnback)
            {
                availableBuffer.Enqueue(value.Current);
            }
            else
            {
                if (value.Current is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
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
                if (availableBuffer.TryDequeue(out T tmpBufferElement))
                {
                    Interlocked.Increment(ref _runningCount);
                    try
                    {
                        bool hc;
                        if (_healthCheckFuncAsync is not null)
                        {
                            hc = await _healthCheckFuncAsync(tmpBufferElement, _cts.Token)
                                        .ConfigureAwait(false);
                        }
                        else
                        {
                            hc = _healthCheckFuncSync(tmpBufferElement, _cts.Token);
                        }
                        if (hc)
                        {
                            availableBuffer.Enqueue(tmpBufferElement);
                            Interlocked.Decrement(ref _runningCount);
                        }
                        else
                        {
                            Interlocked.Decrement(ref _runningCount);
                            if (tmpBufferElement is IDisposable disposable)
                            {
                                disposable.Dispose();
                            }
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Decrement(ref _runningCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Decrement(ref _runningCount);
                        IncrementError("Health Check error", ex);
                    }
                }
            }
            LogRingBuffer($"{Alias} End Health Check background");
        }

        private void ApplyPolicyTimeoutAccquireAsync(string source, TimeSpan basetime, RingBufferTimeoutException exception, RingBufferMetric metric)
        {

            IncrementTimeout();
            if (_policytimeoutAccquire == RingBufferPolicyTimeout.MaximumCapacity)
            {
                if (metric.State.CurrentCapacity >= metric.State.MaximumCapacity && metric.State.CurrentAvailable == 0)
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
                    if (tmout.Value || tmout.Error != null)
                    {
                        LogRingBuffer($"{Alias} trigger policy(UserPolicy) Timeout accquire : {exception.ElapsedTime}/{(long)basetime.TotalMilliseconds}", LogLevel.Warning);
                        TimeoutCallBack?.Invoke(this, new RingBufferTimeoutEventArgs(
                            Alias,
                            nameof(source),
                            exception.ElapsedTime,
                            (long)basetime.TotalMilliseconds,
                            metric));
                    }

                }
                catch (Exception ex)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        IncrementError("Policy(UserPolicy) Timeout accquire error", ex);
                    }
                }
            }
            else if (_policytimeoutAccquire == RingBufferPolicyTimeout.Ignore)
            {
                //none
            }
        }

        private RingBufferValue<T> RingAccquire(TimeSpan? timeout = null)
        {
            var localtmout = TimeoutAccquire;
            if (timeout.HasValue)
            {
                localtmout = timeout.Value;
            }
            var timer = new NaturalTimer();
            timer.ReStart();
            T tmpBufferElement;

            var localsta = CurrentState;
            if (localsta.FailureState)
            {
                timer.Stop();
                NaturalTimer.Delay(1);
                return new RingBufferValue<T>(Alias, localsta, (long)timer.TotalMilliseconds, false, new RingBufferException("Current State Sick", null), default, null);
            }

            timer.ReStart();

            while (!availableBuffer.TryDequeue(out tmpBufferElement))
            {
                if (NaturalTimer.Delay(WaitNextTry, _cts.Token))
                {
                    return new RingBufferValue<T>(Alias, localsta, (long)timer.TotalMilliseconds, false, null, tmpBufferElement, null);
                }
                if (localtmout.TotalMilliseconds != -1 && timer.TotalMilliseconds > localtmout.TotalMilliseconds)
                {
                    timer.Stop();
                    Exception ex = new RingBufferTimeoutException(nameof(RingAccquire), (long)timer.TotalMilliseconds, $"Timeout({localtmout.TotalMilliseconds}) {nameof(RingAccquire)}({timer.TotalMilliseconds:F0})");
                    IncrementTimeout();
                    return new RingBufferValue<T>(Alias, localsta, (long)timer.TotalMilliseconds, false, ex, tmpBufferElement, null);
                }
                IncrementWaitCount();
                localsta = InternalCurrentState;
            }
            //do not move increment. This is critial order imediate after while loop
            Interlocked.Increment(ref _runningCount);

            timer.Stop();
            IncrementAcquisition();

            if (CountSkipDelay > 1)
            {
                NaturalTimer.Delay(1);
                Interlocked.Exchange(ref CountSkipDelay, 0);
            }
            Interlocked.Increment(ref CountSkipDelay);

            var oksta = InternalCurrentState;
            return new RingBufferValue<T>(Alias, oksta, (long)timer.TotalMilliseconds, true, null, tmpBufferElement, RenewContextBuffer);

        }

        private RingBufferMetric CreateMetricReport()
        {
            var sta = CurrentState;
            return new(sta,
                Alias,
                _reportCount.TimeoutCount,
                _reportCount.ErrorCount,
                _reportCount.WaitCount,
                _reportCount.AcquisitionCount,
                _reportCount.AcquisitionSucceeded,
                _reportCount.AverageSucceededExecution,
                _intervalReport);
        }

        private RingBufferMetric CreateMetricAutoScaler(RingBufferState sta)
        {
            return new(
                sta,
                Alias,
                _autoScalercount.TimeoutCount,
                _autoScalercount.ErrorCount,
                _autoScalercount.WaitCount,
                _autoScalercount.AcquisitionCount,
                _autoScalercount.AcquisitionCount,
                TimeSpan.Zero,
                _intervalAutoScaler);
        }

        private async ValueTask RingAutoScaler()
        {
            while (!_cts.IsCancellationRequested)
            {
                //wait interval to next autoscaler
                if (NaturalTimer.Delay(_intervalAutoScaler, () => _stopTaskAutoScaler, _cts.Token))
                {
                    return;
                }
                var staAutoScaler = CurrentState;
                int newAvaliable = staAutoScaler.CurrentCapacity;
                RingBufferMetric metric = CreateMetricAutoScaler(staAutoScaler);

                if (!staAutoScaler.FailureState)
                {
                    try
                    {
                        //user autoscaler
                        if (_autoScaleFuncAsync != null)
                        {
                            newAvaliable = await _autoScaleFuncAsync.Invoke(metric, _cts.Token)
                                    .ConfigureAwait(false);
                        }
                        else
                        {
                            newAvaliable = _autoScaleFuncSync.Invoke(metric, _cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        IncrementError("Auto Scaler error", ex);
                        continue;
                    }
                }
                else
                {
                    //linked race-condition
                    if (_linkedFailureStateFunc != null)
                    {
                        try
                        {
                            if (_linkedFailureStateFunc.Invoke())
                            {
                                IncrementError("Linked Failure State", new RingBufferException("Linked Failure State"));

                                if (NaturalTimer.Delay(IntervalOpenCircuit, () => _stopTaskAutoScaler, _cts.Token))
                                {
                                    return;
                                }
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            IncrementError("Linked Failure State error", ex);
                            if (NaturalTimer.Delay(IntervalOpenCircuit, () => _stopTaskAutoScaler, _cts.Token))
                            {
                                return;
                            }
                            continue;
                        }
                    }
                    //opened
                    var aux = _circuitBreaker.IsCloseCircuit(() => staAutoScaler.CurrentCapacity >= MinimumCapacity);
                    if (!aux.Value)
                    {
                        IncrementError("Factory error", aux.Error);
                        if (NaturalTimer.Delay(IntervalOpenCircuit, () => _stopTaskAutoScaler, _cts.Token))
                        {
                            return;
                        }
                        continue;
                    }
                    else
                    {
                        newAvaliable = MinimumCapacity;
                    }
                }

                if (newAvaliable < MinimumCapacity)
                {
                    newAvaliable = MinimumCapacity;
                }
                if (newAvaliable > MaximumCapacity)
                {
                    newAvaliable = MaximumCapacity;
                }

                await RedefineCapacity(newAvaliable);

                var newmetric = InternalCurrentState;

                if (newmetric.CurrentCapacity != staAutoScaler.CurrentCapacity || _triggerTaskAutoScaler)
                {
                    _triggerTaskAutoScaler = false;
                    LogRingBuffer($"{Alias} End AutoScale, {staAutoScaler.CurrentCapacity} to {newmetric.CurrentCapacity}. Cap./Run./Aval. = {newmetric.CurrentCapacity}/{newmetric.CurrentRunning}/{newmetric.CurrentAvailable} ");
                    AutoScalerCallback?.Invoke(this, new RingBufferAutoScaleEventArgs(Alias, metric.State.CurrentCapacity, newmetric.CurrentCapacity, CreateMetricAutoScaler(newmetric)));
                }
                _autoScalercount.ResetCount();
            }
            LogRingBuffer($"{Alias} End AutoScaler background");
        }

        private async ValueTask RedefineCapacity(int target)
        {
            var sta = InternalCurrentState;
            var diff = target - sta.CurrentCapacity;
            if (sta.CurrentCapacity < sta.MinimumCapacity)
            {
                diff = sta.MinimumCapacity;
            }
            if (_linkedFailureStateFunc != null)
            {
                var hs = _linkedFailureStateFunc.Invoke();
                if (hs)
                {
                    return;
                }
            }

            if (diff == 0)
            {
                return;
            }

            LogRingBuffer($"{Alias} Try AutoScale({diff}) {sta.CurrentCapacity} to {target}.");

            if (diff < 0)
            {
                while (diff < 0 && !_cts.IsCancellationRequested)
                {
                    sta = InternalCurrentState;
                    if (sta.CurrentCapacity <= MinimumCapacity)
                    {
                        break;
                    }
                    if (availableBuffer.TryDequeue(out T deletebuffer))
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
                }
            }

            await TryAddCapacityAsync(diff, _cts.Token);
        }

        private void IncrementTimeout()
        {
            _autoScalercount.IncrementTimeout();
            if (_reportTask != null)
            {
                _reportCount.IncrementTimeout();
            }
        }

        private void IncrementError(string title, Exception ex)
        {
            _autoScalercount.IncrementErrorCount();
            if (_reportTask != null)
            {
                _reportCount.IncrementErrorCount();
            }
            LogRingBuffer($"{Alias} {title}: {ex}", LogLevel.Error);
            ErrorCallBack?.Invoke(this, new RingBufferErrorEventArgs(Alias, ex));
        }

        private void IncrementWaitCount()
        {
            _autoScalercount.IncrementWaitCount();
            if (_reportTask != null)
            {
                _reportCount.IncrementWaitCount();
            }
        }

        private void IncrementAcquisition()
        {
            _autoScalercount.IncrementAcquisition();
            if (_reportTask != null)
            {
                _reportCount.IncrementAcquisition();
            }
        }

        private async Task TryAddCapacityAsync(int addvalue, CancellationToken cancellationToken)
        {
            for (var i = 0; i < addvalue; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    T buff;
                    if (itemFactoryFunc.ExistFuncSync)
                    {
                        buff = itemFactoryFunc.ItemSync(cancellationToken);
                    }
                    else
                    {
                        buff = await itemFactoryFunc.ItemAsync(cancellationToken);
                    }
                    var sta = InternalCurrentState;
                    if (sta.CurrentCapacity > MaximumCapacity)
                    {
                        if (buff is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        break;
                    }
                    else
                    {
                        availableBuffer.Enqueue(buff);
                    }
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    IncrementError("TryAddCapacity Factory error", ex);
                    var sta = CurrentState;
                    if (sta.CurrentCapacity > MinimumCapacity || sta.FailureState)
                    {
                        NaturalTimer.Delay(1);
                        break;
                    }
                }
            }
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
                        _healthCheckTask.Wait();
                        _healthCheckTask.Dispose();
                    }
                    if (_reportTask != null)
                    {
                        _reportTask.Wait();
                        _reportTask.Dispose();
                    }
                    if (_autoScalerTask != null)
                    {
                        _autoScalerTask.Wait();
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
