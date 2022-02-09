using Microsoft.Extensions.Logging;
using RingBufferPlus.Events;
using RingBufferPlus.Exceptions;
using RingBufferPlus.Features;
using RingBufferPlus.ObjectValues;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus
{

    public class RingBuffer<T> : IPropertiesRingBuffer, IRingBuffer<T>, IBuildRingBuffer<T>, IRunningRingBuffer<T>, IDisposable
    {

        #region Private Properties

        private CancellationTokenSource _cts;
        private ManagerRingBuffer<T> _managerRingBuffer;
        private ILogger _logger = null;
        private LogLevel _defaultloglevel = LogLevel.None;
        private ILoggerFactory _loggerFactory = null;
        private TimeSpan _warmupAutoScaler;
        private TimeSpan _intervalAutoScaler;
        private TimeSpan _intervalReport;
        private TimeSpan _intervalHealthCheck;
        private TimeSpan _intervalOpenCircuit;
        private TimeSpan _timeoutAccquire;
        private TimeSpan _idleAccquire;

        private RingBufferPolicyTimeout _policytimeoutAccquire;
        private Func<RingBufferMetric, CancellationToken, bool>? _userpolicytimeoutAccquireFunc;
        private Func<RingBufferMetric, CancellationToken, Task<int>>? _autoScaleFuncAsync;
        private Func<RingBufferMetric, CancellationToken, int>? _autoScaleFuncSync;
        private Func<T, CancellationToken, Task<bool>> _healthCheckFuncAsync;
        private Func<T, CancellationToken, bool> _healthCheckFuncSync;
        private Func<CancellationToken, Task<T>> _factoryAsync;
        private Func<CancellationToken, T> _factorySync;
        private Action<RingBufferMetric, CancellationToken> _reportSync;
        private Func<RingBufferMetric, CancellationToken, Task> _reportAsync;
        private Func<bool> _linkedFailureStateFunc;
        private bool _userAutoScaler;
        private bool _disposedValue;

        #endregion

        #region Constructor

        public static IRingBuffer<T> CreateBuffer(int value = 2)
        {
            if (value <= 1) throw new RingBufferException("InitialBuffer must be greater than 1");
            return new RingBuffer<T>(value);
        }

        internal RingBuffer(int value)
        {

            _timeoutAccquire = TimeSpan.Zero;
            _idleAccquire = TimeSpan.Zero;
            WaitNextTry = TimeSpan.Zero;
            _policytimeoutAccquire = RingBufferPolicyTimeout.EveryTime;
            _intervalAutoScaler = TimeSpan.Zero;
            _warmupAutoScaler = TimeSpan.Zero;
            _intervalHealthCheck = TimeSpan.Zero;
            _intervalReport = TimeSpan.Zero;
            _intervalOpenCircuit = TimeSpan.Zero;
            InitialCapacity = value;
            MinimumCapacity = value;
            MaximumCapacity = value;

        }

        #endregion

        #region IPropertiesRingBuffer

        public RingBufferState CurrentState
        {
            get
            {
                return _managerRingBuffer.CreateState();
            }
        }
        public RingBufferPolicyTimeout PolicyTimeout => _policytimeoutAccquire;
        public TimeSpan IntervalHealthCheck => _intervalHealthCheck;
        public TimeSpan IntervalAutoScaler => _intervalAutoScaler;
        public TimeSpan IntervalReport => _intervalReport;
        public TimeSpan IdleAccquire => _idleAccquire;
        public TimeSpan TimeoutAccquire => _timeoutAccquire;
        public TimeSpan WaitNextTry { get; private set; }
        public TimeSpan IntervalFailureState => _intervalOpenCircuit;
        public bool HasPolicyTimeout => _userpolicytimeoutAccquireFunc != null;
        public bool HasHealthCheck => _healthCheckFuncSync != null || _healthCheckFuncAsync != null;
        public bool HasAutoScaler => _userAutoScaler;
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

            _managerRingBuffer = new(Alias, MinimumCapacity, MaximumCapacity, _logger, _defaultloglevel, _linkedFailureStateFunc, _cts.Token);
            _managerRingBuffer.UsingEventError(ErrorCallBack);
            _managerRingBuffer.UsingEventAutoScaler(AutoScalerCallback);
            _managerRingBuffer.UsingEventTimeout(TimeoutCallBack);
            if (_reportSync != null || _reportAsync != null)
            {
                _managerRingBuffer.UsingReport(_reportSync, _reportAsync, _intervalReport);
            }
            _managerRingBuffer.UsingHealthCheck(_healthCheckFuncSync, _healthCheckFuncAsync, _intervalHealthCheck);
            _managerRingBuffer.UsingRedefineCapacity(_factorySync, _factoryAsync, _intervalAutoScaler);
            _managerRingBuffer.StartCapacity(InitialCapacity);
            _managerRingBuffer.UsingAutoScaler(_autoScaleFuncSync, _autoScaleFuncAsync, _intervalAutoScaler, _intervalOpenCircuit, _warmupAutoScaler);

            return this;
        }

        #endregion

        #region IRunningRingBuffer

        public RingBufferValue<T> Accquire(TimeSpan? timeout = null, CancellationToken? cancellation = null)
        {
            var localtimeout = _timeoutAccquire;
            if (timeout.HasValue)
            {
                localtimeout = timeout.Value;
            }
            return _managerRingBuffer.ReadBuffer(localtimeout, _idleAccquire, _policytimeoutAccquire, _userpolicytimeoutAccquireFunc, cancellation);
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

            if (_factorySync == null && _factoryAsync == null)
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

            if (IdleAccquire.TotalMilliseconds == 0)
            {
                _idleAccquire = DefaultValues.WaitTimeAvailable;
                LogRingBuffer($"{Alias} using default Idle Accquire {IdleAccquire}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user Idle Accquire {IdleAccquire}");
            }

            if (TimeoutAccquire.TotalMilliseconds == 0)
            {
                _timeoutAccquire = DefaultValues.TimeoutAccquire;
                LogRingBuffer($"{Alias} using default TimeoutAccquire {TimeoutAccquire}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user TimeoutAccquire {TimeoutAccquire}");
            }

            if (_intervalOpenCircuit.TotalMilliseconds == 0)
            {
                _intervalOpenCircuit = DefaultValues.IntervalFailureState;
                LogRingBuffer($"{Alias} using default IntervalOpenCircuit {IntervalFailureState}");
            }
            else
            {
                LogRingBuffer($"{Alias} using user IntervalOpenCircuit {IntervalFailureState}");
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

        public IRingBuffer<T> AddLogProvider(ILoggerFactory value, RingBufferLogLevel defaultlevel = RingBufferLogLevel.Trace)
        {
            _defaultloglevel = (LogLevel)Enum.Parse(typeof(LogLevel), defaultlevel.ToString(), true);
            _loggerFactory = value;
            return this;
        }

        public IRingBuffer<T> SetIntervalFailureState(long mileseconds)
        {
            return SetIntervalFailureState(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> SetIntervalFailureState(TimeSpan value)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Interval Open Circuit must be greater than zero");
            _intervalOpenCircuit = value;
            return this;
        }

        public IRingBuffer<T> SetTimeoutAccquire(long mileseconds, long? idle)
        {
            var localidle = DefaultValues.WaitTimeAvailable;
            if (idle.HasValue)
            {
                localidle = TimeSpan.FromMilliseconds(idle.Value);
            }
            return SetTimeoutAccquire(TimeSpan.FromMilliseconds(mileseconds), localidle);
        }

        public IRingBuffer<T> SetTimeoutAccquire(TimeSpan value, TimeSpan? idle)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Timeout Available must be greater than zero");
            var localidle = DefaultValues.WaitTimeAvailable;
            if (idle.HasValue)
            {
                if (idle.Value.TotalMilliseconds <= 0) throw new RingBufferException("Idle Accquire must be greater than zero");
                localidle = idle.Value;
            }
            _timeoutAccquire = value;
            _idleAccquire = localidle;
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
            _factoryAsync = value;
            _factorySync = null;
            return this;
        }

        public IRingBuffer<T> Factory(Func<CancellationToken, T> value)
        {
            if (value is null) throw new RingBufferException("Factory can't be null");
            _factoryAsync = null;
            _factorySync = value;
            return this;
        }

        public IRingBuffer<T> SetIntervalHealthCheck(long mileseconds)
        {
            return SetIntervalHealthCheck(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> SetIntervalHealthCheck(TimeSpan value)
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

        public IRingBuffer<T> SetIntervalAutoScaler(long mileseconds, long? warmup = null)
        {
            var localwarmup = TimeSpan.Zero;
            if (warmup.HasValue)
            {
                if (warmup <= 0) throw new RingBufferException("Interval warmup must be greater than zero");
                localwarmup = TimeSpan.FromMilliseconds(warmup.Value);
            }
            return SetIntervalAutoScaler(TimeSpan.FromMilliseconds(mileseconds), localwarmup);
        }

        public IRingBuffer<T> SetIntervalAutoScaler(TimeSpan value,TimeSpan ? warmup = null)
        {
            if (value.TotalMilliseconds <= 0) throw new RingBufferException("Interval AutoScaler must be greater than zero");
            var localwarmup = TimeSpan.Zero;
            if (warmup.HasValue)
            {
                if (warmup.Value.TotalMilliseconds <= 0) throw new RingBufferException("Interval warmup must be greater than zero");
                localwarmup = warmup.Value;
            }
            _intervalAutoScaler = value;
            _warmupAutoScaler = localwarmup;
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

        public IRingBuffer<T> SetPolicyTimeout(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
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

        public IRingBuffer<T> SetIntervalReport(long mileseconds)
        {
            return SetIntervalReport(TimeSpan.FromMilliseconds(mileseconds));
        }

        public IRingBuffer<T> SetIntervalReport(TimeSpan value)
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

        #endregion

        #region IDispose

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing && _cts is not null)
                {
                    _cts.Cancel();
                    _managerRingBuffer.Dispose();
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
