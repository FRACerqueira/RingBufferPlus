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
        private RingBufferManager<T> _managerRingBuffer;
        private ILogger _logger = null;
        private LogLevel _defaultloglevel = LogLevel.None;
        private ILoggerFactory _loggerFactory = null;
        private TimeSpan _warmupAutoScaler;
        private TimeSpan _intervalAutoScaler;
        private TimeSpan _intervalReport;
        private TimeSpan _intervalHealthCheck;
        private TimeSpan _intervalFailureState;
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
            if (value <= 1) throw CreateException(MessagesResource.BuildErr_InitialBuffer);
            return new RingBuffer<T>(value);
        }

        internal RingBuffer(int value)
        {

            _timeoutAccquire = TimeSpan.Zero;
            _idleAccquire = TimeSpan.Zero;
            _policytimeoutAccquire = RingBufferPolicyTimeout.EveryTime;
            _intervalAutoScaler = TimeSpan.Zero;
            _warmupAutoScaler = TimeSpan.Zero;
            _intervalHealthCheck = TimeSpan.Zero;
            _intervalReport = TimeSpan.Zero;
            _intervalFailureState = TimeSpan.Zero;
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
        public TimeSpan WarmupAutoScaler => _warmupAutoScaler;
        public TimeSpan IntervalReport => _intervalReport;
        public TimeSpan IdleAccquire => _idleAccquire;
        public TimeSpan TimeoutAccquire => _timeoutAccquire;
        public TimeSpan IntervalFailureState => _intervalFailureState;
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
            _managerRingBuffer.UsingAutoScaler(_autoScaleFuncSync, _autoScaleFuncAsync, _intervalAutoScaler, _intervalFailureState, _warmupAutoScaler);

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
            _linkedFailureStateFunc = value ?? throw CreateException(MessagesResource.BuildErr_LinkedFunction);
            return this;
        }

        public IBuildRingBuffer<T> Build()
        {
            if (string.IsNullOrEmpty(Alias))
            {
                Alias = $"RingBuffer.{typeof(T).Name}";
            }

            if (_loggerFactory != null)
            {
                _logger = _loggerFactory.CreateLogger(Alias);
            }

            if (_factorySync == null && _factoryAsync == null)
            {
                var err = CreateException(MessagesResource.BuildErr_Factory);
                LogRingBuffer(string.Format(MessagesResource.FatalError,err.ToString()), LogLevel.Error);
                throw err;
            }

            if (InitialCapacity <= 1)
            {
                var err = CreateException(MessagesResource.BuildErr_InitialBuffer); ;
                LogRingBuffer(string.Format(MessagesResource.FatalError, err.ToString()), LogLevel.Error);
                throw err;
            }

            if (IdleAccquire.TotalMilliseconds == 0)
            {
                _idleAccquire = DefaultValues.WaitTimeAvailable;
            }

            if (TimeoutAccquire.TotalMilliseconds == 0)
            {
                _timeoutAccquire = DefaultValues.TimeoutAccquire;
            }

            if (_intervalFailureState.TotalMilliseconds == 0)
            {
                _intervalFailureState = DefaultValues.IntervalFailureState;
            }

            if (MinimumCapacity < 0)
            {
                if (MinimumCapacity < 0)
                {
                    MinimumCapacity = InitialCapacity;
                }
            }
            if (MaximumCapacity < 0)
            {
                if (MaximumCapacity < 0)
                {
                    MaximumCapacity = InitialCapacity;
                }
            }

            if (MinimumCapacity > InitialCapacity)
            {
                var err = CreateException(string.Format(MessagesResource.BuildErr_MinInit,Alias,MinimumCapacity,InitialCapacity));
                LogRingBuffer(string.Format(MessagesResource.FatalError, err.ToString()), LogLevel.Error);
                throw err;
            }

            if (MaximumCapacity < InitialCapacity)
            {
                var err = CreateException(string.Format(MessagesResource.BuildErr_MaxInit, Alias, MaximumCapacity, InitialCapacity));
                LogRingBuffer(string.Format(MessagesResource.FatalError, err.ToString()), LogLevel.Error);
                throw err;
            }

            if (MaximumCapacity < MinimumCapacity)
            {
                var err = CreateException(string.Format(MessagesResource.BuildErr_MaxMin, Alias, MaximumCapacity, MinimumCapacity));
                LogRingBuffer(string.Format(MessagesResource.FatalError, err.ToString()), LogLevel.Error);
                throw err;
            }

            _userAutoScaler = _autoScaleFuncSync != null || _autoScaleFuncAsync != null;
            if (!_userAutoScaler)
            {
                _autoScaleFuncSync = (_, _) => InitialCapacity;
            }

            if (_intervalReport.TotalMilliseconds == 0)
            {
                _intervalReport = DefaultValues.IntervalReport;
            }

            if (_intervalHealthCheck.TotalMilliseconds == 0)
            {
                _intervalHealthCheck = DefaultValues.IntervalHealthcheck;
            }

            if (_intervalAutoScaler.TotalMilliseconds == 0)
            {
                _intervalAutoScaler = DefaultValues.IntervalScaler;
            }

            LogRingBuffer(string.Format(MessagesResource.Log_Alias, Alias));
            LogRingBuffer(string.Format(MessagesResource.Log_InitialCapacity, Alias, InitialCapacity.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_MinimumCapacity, Alias, MinimumCapacity.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_MaximumCapacity, Alias, MaximumCapacity.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_IdleAccquire, Alias, _idleAccquire.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_TimeoutAccquire, Alias, _timeoutAccquire.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_IntervalFailureState, Alias, _intervalFailureState.ToString()));
            if (!_userAutoScaler)
            {
                LogRingBuffer(string.Format(MessagesResource.Log_FixedAutoScaler, Alias, InitialCapacity.ToString()));
            }
            LogRingBuffer(string.Format(MessagesResource.Log_IntervalReport, Alias, _intervalReport.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_IntervalHealthCheck, Alias, _intervalHealthCheck.ToString()));
            LogRingBuffer(string.Format(MessagesResource.Log_IntervalAutoScaler, Alias, _intervalAutoScaler.ToString()));

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
            if (value.TotalMilliseconds <= 0) throw CreateException(string.Format(MessagesResource.BuildErr_IntervalFailureState,Alias));
            _intervalFailureState = value;
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
            if (value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_TimeoutAccquire);
            var localidle = DefaultValues.WaitTimeAvailable;
            if (idle.HasValue)
            {
                if (idle.Value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_IdleAccquire);
                localidle = idle.Value;
            }
            _timeoutAccquire = value;
            _idleAccquire = localidle;
            return this;
        }

        public IRingBuffer<T> AliasName(string value)
        {
            if (string.IsNullOrEmpty(value)) throw CreateException(MessagesResource.BuildErr_Alias);
            Alias = value;
            return this;
        }

        public IRingBuffer<T> InitialBuffer(int value)
        {
            if (value <= 1) throw CreateException(MessagesResource.BuildErr_InitialBuffer);
            InitialCapacity = value;
            if (MaximumCapacity < InitialCapacity)
            {
                MaximumCapacity = InitialCapacity;
            }
            return this;
        }

        public IRingBuffer<T> MaxBuffer(int value)
        {
            if (value <= 1) throw CreateException(MessagesResource.BuildErr_MaxAvaliable);
            MaximumCapacity = value;
            return this;
        }

        public IRingBuffer<T> MinBuffer(int value)
        {
            if (value <= 1) throw CreateException(MessagesResource.BuildErr_MinAvaliable);
            MinimumCapacity = value;
            return this;
        }

        public IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_Factory);
            _factoryAsync = value;
            _factorySync = null;
            return this;
        }

        public IRingBuffer<T> Factory(Func<CancellationToken, T> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_Factory);
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
            if (value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_IntervalHealthCheck);
            _intervalHealthCheck = value;
            return this;
        }

        public IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_HealthCheck);
            _healthCheckFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_HealthCheck);
            _healthCheckFuncSync = value;
            return this;
        }

        public IRingBuffer<T> SetIntervalAutoScaler(long mileseconds, long? warmup = null)
        {
            var localwarmup = TimeSpan.Zero;
            if (warmup.HasValue)
            {
                if (warmup <= 0) throw CreateException(MessagesResource.BuildErr_IntervalWarmup);
                localwarmup = TimeSpan.FromMilliseconds(warmup.Value);
            }
            return SetIntervalAutoScaler(TimeSpan.FromMilliseconds(mileseconds), localwarmup);
        }

        public IRingBuffer<T> SetIntervalAutoScaler(TimeSpan value,TimeSpan ? warmup = null)
        {
            if (value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_IntervalAutoScaler);
            var localwarmup = TimeSpan.Zero;
            if (warmup.HasValue)
            {
                if (warmup.Value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_IntervalWarmup);
                localwarmup = warmup.Value;
            }
            _intervalAutoScaler = value;
            _warmupAutoScaler = localwarmup;
            return this;
        }

        public IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_AutoScaler);
            _autoScaleFuncAsync = value;
            return this;
        }

        public IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> value)
        {
            if (value is null) throw CreateException(MessagesResource.BuildErr_AutoScaler);
            _autoScaleFuncSync = value;
            return this;
        }

        public IRingBuffer<T> SetPolicyTimeout(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null)
        {
            if (policy == RingBufferPolicyTimeout.UserPolicy)
            {
                if (userpolicy is null) throw CreateException(MessagesResource.BuildErr_PolicyNull);
            }
            else
            {
                if (userpolicy is not null) throw CreateException(MessagesResource.BuildErr_PolicyMustBeNull);
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
            if (value.TotalMilliseconds <= 0) throw CreateException(MessagesResource.BuildErr_IntervalReport);
            _intervalReport = value;
            return this;
        }

        public IRingBuffer<T> MetricsReport(Action<RingBufferMetric, CancellationToken> report)
        {
            if (report is null) throw CreateException(MessagesResource.BuildErr_Report);
            _reportSync = report;
            return this;
        }

        public IRingBuffer<T> MetricsReportAsync(Func<RingBufferMetric, CancellationToken, Task> report)
        {
            if (report is null) throw CreateException(MessagesResource.BuildErr_Report);
            _reportAsync = report;
            return this;
        }

        #endregion

        #region Private Methods

        private static RingBufferException CreateException(string message, Exception innerexception = null)
        {
            return new RingBufferException(message, innerexception);
        }

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
