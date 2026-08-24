// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    // One mutable builder backs all three public views (ADR007): the mode-switch methods
    // (FixedCapacity/ElasticCapacity) narrow which interface the caller sees next, so the
    // compiler enforces mutual exclusivity even though a single instance implements everything.
    // Since ADR007V03, ElasticCapacity is the only elastic view - the floor guard, backlog-
    // reactive signal, and Monitor are unconditionally active for it, so there is no further
    // automatic-vs-manual split (the former IRingBufferAutoScaleBuilder<T>/AutoScaleAcquireFault
    // pair is gone). Explicit interface implementation is required wherever the same method name
    // returns a different interface type depending on which view is in scope.
    internal sealed class RingBufferBuilder<T> :
        IRingBufferBuilder<T>,
        IRingBufferFixedBuilder<T>,
        IRingBufferElasticBuilder<T>
    {
        #region Fields

        private readonly string _uniqueName;
        private ILogger? _logger;
        private int _initcapacity;
        private int _minCapacity;
        private int _maxCapacity;
        private int _sampleUnit;
        private bool _elastic;
        private bool _lockWhenScaling;

        private TimeSpan _samplebasetime;
        private TimeSpan _factoryTimeout;
        private TimeSpan _pulseHeartBeat;
        private TimeSpan _acquireTimeout;
        private byte _maxConsecutiveFactoryFailures;
        private int _maxConcurrentFactoryCalls;

        private double _monitorPercentileP;
        private double _monitorSafetyBuffer;
        private double _monitorHorizon;
        private int _monitorDeadband;

        private Action<Exception>? _errorHandler;
        private Func<T, bool>? _bufferHeartBeat;
        private Func<CancellationToken, Task<T>>? _factory;

        #endregion

        #region Constructor

        public RingBufferBuilder(string uniqueName, ILoggerFactory? loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(uniqueName, nameof(uniqueName));
            _uniqueName = uniqueName;
            _logger = loggerFactory?.CreateLogger(_uniqueName);
            _initcapacity = _minCapacity = _maxCapacity = RingBufferDefault.Capacity;
            _factoryTimeout = RingBufferDefault.FactoryTimeout;
            _pulseHeartBeat = RingBufferDefault.PulseHeartBeat;
            _samplebasetime = RingBufferDefault.SamplesBaseTime;
            _sampleUnit = RingBufferDefault.SampleUnit;
            _acquireTimeout = RingBufferDefault.AcquireTimeout;
            _maxConcurrentFactoryCalls = RingBufferDefault.MaxConcurrentFactoryCalls;
            _monitorPercentileP = RingBufferDefault.MonitorPercentileP;
            _monitorSafetyBuffer = RingBufferDefault.MonitorSafetyBuffer;
            _monitorHorizon = RingBufferDefault.MonitorHorizon;
            _monitorDeadband = RingBufferDefault.MonitorDeadband;
        }

        #endregion

        #region shared mutators

        private void SetFactory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures)
        {
            _factory = value;
            _factoryTimeout = timeout ?? RingBufferDefault.FactoryTimeout;
            _maxConsecutiveFactoryFailures = maxConsecutiveFactoryFailures;
        }

        private void SetHeartBeat(Func<T, bool> value, TimeSpan? pulse)
        {
            _bufferHeartBeat = value;
            _pulseHeartBeat = pulse ?? RingBufferDefault.PulseHeartBeat;
        }

        private void SetLogger(ILogger? value) => _logger = value;

        private void SetAcquireTimeout(TimeSpan value) => _acquireTimeout = value;

        private void SetOnError(Action<Exception> errorHandler) => _errorHandler = errorHandler;

        private void SetLockWhenScaling(bool value) => _lockWhenScaling = value;

        private void SetMonitorTuning(double percentileP, double safetyBuffer, double horizon, int deadband)
        {
            _monitorPercentileP = percentileP;
            _monitorSafetyBuffer = safetyBuffer;
            _monitorHorizon = horizon;
            _monitorDeadband = deadband;
        }

        #endregion

        #region IRingBufferBuilder<T>

        IRingBufferBuilder<T> IRingBufferBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.HeartBeat(Func<T, bool> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.OnError(Action<Exception> errorHandler) { SetOnError(errorHandler); return this; }

        IRingBufferFixedBuilder<T> IRingBufferBuilder<T>.FixedCapacity(int value)
        {
            _initcapacity = _minCapacity = _maxCapacity = value;
            _elastic = false;
            return this;
        }

        IRingBufferElasticBuilder<T> IRingBufferBuilder<T>.ElasticCapacity(int minCapacity, int maxCapacity, int? target, int? numberSamples, TimeSpan? baseTimer, int? maxConcurrentFactoryCalls)
        {
            _minCapacity = minCapacity;
            _maxCapacity = maxCapacity;
            _initcapacity = target ?? minCapacity;
            _sampleUnit = numberSamples ?? RingBufferDefault.SampleUnit;
            _samplebasetime = baseTimer ?? RingBufferDefault.SamplesBaseTime;
            _maxConcurrentFactoryCalls = maxConcurrentFactoryCalls ?? RingBufferDefault.MaxConcurrentFactoryCalls;
            _elastic = true;
            return this;
        }

        #endregion

        #region IRingBufferFixedBuilder<T>

        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.HeartBeat(Func<T, bool> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.OnError(Action<Exception> errorHandler) { SetOnError(errorHandler); return this; }

        IRingBufferService<T> IRingBufferFixedBuilder<T>.Build(CancellationToken cancellation) => BuildCore(cancellation);

        async Task<IRingBufferService<T>> IRingBufferFixedBuilder<T>.BuildWarmupAsync(CancellationToken cancellation)
        {
            var srv = BuildCore(cancellation);
            await srv.WarmupAsync(cancellation).ConfigureAwait(false);
            return srv;
        }

        #endregion

        #region IRingBufferElasticBuilder<T>

        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.HeartBeat(Func<T, bool> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.OnError(Action<Exception> errorHandler) { SetOnError(errorHandler); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.LockWhenScaling(bool value) { SetLockWhenScaling(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.MonitorTuning(double percentileP, double safetyBuffer, double horizon, int deadband) { SetMonitorTuning(percentileP, safetyBuffer, horizon, deadband); return this; }

        IRingBufferManualScaleService<T> IRingBufferElasticBuilder<T>.Build(CancellationToken cancellation) => BuildCore(cancellation);

        async Task<IRingBufferManualScaleService<T>> IRingBufferElasticBuilder<T>.BuildWarmupAsync(CancellationToken cancellation)
        {
            var srv = BuildCore(cancellation);
            await srv.WarmupAsync(cancellation).ConfigureAwait(false);
            return srv;
        }

        #endregion

        #region build

        private RingBufferManager<T> BuildCore(CancellationToken token)
        {
            ValidateBuild();
            LogMessage("Build successfully and Created RingBuffer Manager");
            return new RingBufferManager<T>(token)
            {
                Name = _uniqueName,
                Capacity = _initcapacity,
                MinCapacity = _elastic ? _minCapacity : _initcapacity,
                MaxCapacity = _elastic ? _maxCapacity : _initcapacity,
                FactoryTimeout = _factoryTimeout,
                MaxConsecutiveFactoryFailures = _maxConsecutiveFactoryFailures,
                MaxConcurrentFactoryCalls = _maxConcurrentFactoryCalls,
                PulseHeartBeat = _pulseHeartBeat,
                SamplesBase = _samplebasetime,
                SamplesCount = _sampleUnit,
                Elastic = _elastic,
                MonitorPercentileP = _monitorPercentileP,
                MonitorSafetyBuffer = _monitorSafetyBuffer,
                MonitorHorizon = _monitorHorizon,
                MonitorDeadband = _monitorDeadband,
                AcquireTimeout = _acquireTimeout,
                LockWhenScaling = _lockWhenScaling,
                Logger = _logger,
                ErrorHandler = _errorHandler,
                BufferHeartBeat = _bufferHeartBeat,
                Factory = _factory!
            };
        }

        private void ValidateBuild()
        {
            if (_factory is null)
            {
                var err = new InvalidOperationException("The command Factory is not defined.");
                LogError(err);
                throw err;
            }
            if (_initcapacity < 2)
            {
                var err = new InvalidOperationException("The capacity is less than 2.");
                LogError(err);
                throw err;
            }
            // Round 1 (Resiliência, v6 pre-release audit): PulseHeartBeat now sustains three
            // separate disposal bounds (DisposeOneItemDefensivelyAsync's grace period, the
            // heartbeat pump's own dispose bound, and the pulse timeout itself), for every buffer
            // regardless of Elastic/HeartBeat configuration (DisposeAsync's own drain loop uses it
            // too) - an explicit zero/negative value (a plausible unit mistake) would make every
            // defensive dispose expire instantly, treating ordinary disposal as hung.
            if (_pulseHeartBeat <= TimeSpan.Zero)
            {
                var err = new InvalidOperationException("The pulse (PulseHeartBeat) must be greater than zero.");
                LogError(err);
                throw err;
            }
            if (_elastic)
            {
                if (_minCapacity < 2)
                {
                    var err = new InvalidOperationException("The min capacity is less than 2.");
                    LogError(err);
                    throw err;
                }
                if (_maxCapacity < 2)
                {
                    var err = new InvalidOperationException("The max capacity is less than 2.");
                    LogError(err);
                    throw err;
                }
                if (_minCapacity > _maxCapacity)
                {
                    var err = new InvalidOperationException("The min capacity is greater than the max capacity.");
                    LogError(err);
                    throw err;
                }
                if (_minCapacity > _initcapacity)
                {
                    var err = new InvalidOperationException("The min capacity is greater than target.");
                    LogError(err);
                    throw err;
                }
                if (_maxCapacity < _initcapacity)
                {
                    var err = new InvalidOperationException("The max capacity is less than target.");
                    LogError(err);
                    throw err;
                }
                if (_sampleUnit < 1)
                {
                    var err = new InvalidOperationException("numberSamples in command ElasticCapacity must be greater or equal 1");
                    LogError(err);
                    throw err;
                }
                if (_samplebasetime.TotalMilliseconds / _sampleUnit < 100)
                {
                    var err = new InvalidOperationException("baseTimer / numberSamples in command ElasticCapacity must be greater or equal 100ms");
                    LogError(err);
                    throw err;
                }
                if (_maxConcurrentFactoryCalls < 1)
                {
                    var err = new InvalidOperationException("maxConcurrentFactoryCalls in command ElasticCapacity must be greater or equal 1");
                    LogError(err);
                    throw err;
                }
                if (_monitorPercentileP <= 0 || _monitorPercentileP > 1)
                {
                    var err = new InvalidOperationException("percentileP in command MonitorTuning must be greater than 0 and less than or equal to 1");
                    LogError(err);
                    throw err;
                }
                if (_monitorSafetyBuffer < 0)
                {
                    var err = new InvalidOperationException("safetyBuffer in command MonitorTuning must be greater or equal 0");
                    LogError(err);
                    throw err;
                }
                if (_monitorHorizon < 0)
                {
                    var err = new InvalidOperationException("horizon in command MonitorTuning must be greater or equal 0");
                    LogError(err);
                    throw err;
                }
                if (_monitorDeadband < 0)
                {
                    var err = new InvalidOperationException("deadband in command MonitorTuning must be greater or equal 0");
                    LogError(err);
                    throw err;
                }
            }
        }

        #endregion

        private void LogMessage(string message)
        {
            // Round 2 (Estabilidade, v6 pre-release audit): IsEnabled itself is a call into
            // untrusted external code (same F23 class as SafeInvokeSink below) and can throw -
            // guarded via SafeIsEnabled rather than called raw, same fix already applied to the
            // sibling gap this one was modeled after in RingBufferManager.LogMessage.
            if (_logger is null || !SafeIsEnabled(_logger, LogLevel.Debug)) return;

            SafeInvokeSink(() => logMessageForDbg(_logger, _uniqueName, message, null));
        }

        private void LogError(Exception message)
        {
            // Fixed alongside the OnError signature change (ADR007V03): this guard previously
            // required _logger to be non-null AND enabled for Error, which meant a caller who
            // configured OnError without also configuring Logger never had their error handler
            // invoked at all during Build-time validation - the exact opposite of "the logger is
            // already configured separately" (ADR007V03's own reasoning for simplifying OnError).
            if (_logger is null && _errorHandler is null) return;

            if (_errorHandler is null)
            {
                if (SafeIsEnabled(_logger!, LogLevel.Error))
                {
                    // Round 2 (Observabilidade, v6 pre-release audit): passed null here instead of
                    // the real exception, unlike RingBufferManager's own LogError - a structured
                    // sink (Application Insights, Serilog) reading the canonical Exception field
                    // got nothing for a builder validation error, only the text embedded in the
                    // message. Round 3: that text was message.ToString() (type + message + stack
                    // trace) - far more verbose than RingBufferManager.LogError's own text field
                    // (just error.Message), a format asymmetry a structured sink reading the text
                    // field alone would see as inconsistent between the two classes. Narrowed to
                    // message.Message to match - the full exception (type, stack trace) is already
                    // available via the Exception argument now passed alongside it.
                    SafeInvokeSink(() => logMessageForErr(_logger!, _uniqueName, message.Message, message));
                }
            }
            else
            {
                SafeInvokeSink(() => _errorHandler.Invoke(message));
            }
        }

        private static bool SafeIsEnabled(ILogger logger, LogLevel level)
        {
            try
            {
                return logger.IsEnabled(level);
            }
            catch
            {
                return false;
            }
        }

        // A user-supplied Logger/OnError is untrusted external code (Round 7, unguarded-callback
        // sweep - same class as F23, found in RingBufferManager): if it throws while ValidateBuild
        // is reporting a real validation failure, that throw must not replace/mask the actual
        // exception ValidateBuild is about to throw to its own caller.
        private static void SafeInvokeSink(Action invoke)
        {
            try
            {
                invoke();
            }
            catch
            {
                //ignore: the logging/error sink itself threw - nothing further can be logged about it
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferBuilder({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferBuilder({Source}) : {Message}");
    }
}
