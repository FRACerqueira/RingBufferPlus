// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    // One mutable builder backs all four public views (ADR007): the mode-switch methods
    // (FixedCapacity/ElasticCapacity/AutoScaleAcquireFault) narrow which interface the caller
    // sees next, so the compiler enforces mutual exclusivity even though a single instance
    // implements everything. Explicit interface implementation is required wherever the same
    // method name returns a different interface type depending on which view is in scope.
    internal sealed class RingBufferBuilder<T> :
        IRingBufferBuilder<T>,
        IRingBufferFixedBuilder<T>,
        IRingBufferElasticBuilder<T>,
        IRingBufferAutoScaleBuilder<T>
    {
        #region Fields

        private readonly string _uniqueName;
        private ILogger? _logger;
        private int _initcapacity;
        private int _minCapacity;
        private int _maxCapacity;
        private int _sampleUnit;
        private bool _elastic;
        private bool _autoScaleFault;
        private byte _numberFault;
        private bool _backgroundLogger;
        private bool _lockWhenScaling;

        private TimeSpan _samplebasetime;
        private TimeSpan _factoryTimeout;
        private TimeSpan _pulseHeartBeat;
        private TimeSpan _acquireTimeout;
        private byte _maxConsecutiveFactoryFailures;
        private int _maxConcurrentFactoryCalls;

        private Action<ILogger?, Exception>? _errorHandler;
        private Action<RingBufferValue<T>>? _bufferHeartBeat;
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
        }

        #endregion

        #region shared mutators

        private void SetFactory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures)
        {
            _factory = value;
            _factoryTimeout = timeout ?? RingBufferDefault.FactoryTimeout;
            _maxConsecutiveFactoryFailures = maxConsecutiveFactoryFailures;
        }

        private void SetHeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse)
        {
            _bufferHeartBeat = value;
            _pulseHeartBeat = pulse ?? RingBufferDefault.PulseHeartBeat;
        }

        private void SetLogger(ILogger? value) => _logger = value;

        private void SetBackgroundLogger(bool value) => _backgroundLogger = value;

        private void SetAcquireTimeout(TimeSpan value) => _acquireTimeout = value;

        private void SetOnError(Action<ILogger?, Exception> errorHandler) => _errorHandler = errorHandler;

        private void SetLockWhenScaling(bool value) => _lockWhenScaling = value;

        #endregion

        #region IRingBufferBuilder<T>

        IRingBufferBuilder<T> IRingBufferBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.BackgroundLogger(bool value) { SetBackgroundLogger(value); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferBuilder<T> IRingBufferBuilder<T>.OnError(Action<ILogger?, Exception> errorHandler) { SetOnError(errorHandler); return this; }

        IRingBufferFixedBuilder<T> IRingBufferBuilder<T>.FixedCapacity(int value)
        {
            _initcapacity = _minCapacity = _maxCapacity = value;
            _elastic = false;
            return this;
        }

        IRingBufferElasticBuilder<T> IRingBufferBuilder<T>.ElasticCapacity(int initialCapacity, int minCapacity, int maxCapacity, int? numberSamples, TimeSpan? baseTimer, int? maxConcurrentFactoryCalls)
        {
            _initcapacity = initialCapacity;
            _minCapacity = minCapacity;
            _maxCapacity = maxCapacity;
            _sampleUnit = numberSamples ?? RingBufferDefault.SampleUnit;
            _samplebasetime = baseTimer ?? RingBufferDefault.SamplesBaseTime;
            _maxConcurrentFactoryCalls = maxConcurrentFactoryCalls ?? RingBufferDefault.MaxConcurrentFactoryCalls;
            _elastic = true;
            return this;
        }

        #endregion

        #region IRingBufferFixedBuilder<T>

        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.BackgroundLogger(bool value) { SetBackgroundLogger(value); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferFixedBuilder<T> IRingBufferFixedBuilder<T>.OnError(Action<ILogger?, Exception> errorHandler) { SetOnError(errorHandler); return this; }

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
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.BackgroundLogger(bool value) { SetBackgroundLogger(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.OnError(Action<ILogger?, Exception> errorHandler) { SetOnError(errorHandler); return this; }
        IRingBufferElasticBuilder<T> IRingBufferElasticBuilder<T>.LockWhenScaling(bool value) { SetLockWhenScaling(value); return this; }

        IRingBufferAutoScaleBuilder<T> IRingBufferElasticBuilder<T>.AutoScaleAcquireFault(byte numberOfFaults)
        {
            _autoScaleFault = true;
            _numberFault = numberOfFaults;
            return this;
        }

        IRingBufferManualScaleService<T> IRingBufferElasticBuilder<T>.Build(CancellationToken cancellation) => BuildCore(cancellation);

        async Task<IRingBufferManualScaleService<T>> IRingBufferElasticBuilder<T>.BuildWarmupAsync(CancellationToken cancellation)
        {
            var srv = BuildCore(cancellation);
            await srv.WarmupAsync(cancellation).ConfigureAwait(false);
            return srv;
        }

        #endregion

        #region IRingBufferAutoScaleBuilder<T>

        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout, byte maxConsecutiveFactoryFailures) { SetFactory(value, timeout, maxConsecutiveFactoryFailures); return this; }
        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse) { SetHeartBeat(value, pulse); return this; }
        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.Logger(ILogger? value) { SetLogger(value); return this; }
        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.BackgroundLogger(bool value) { SetBackgroundLogger(value); return this; }
        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.AcquireTimeout(TimeSpan value) { SetAcquireTimeout(value); return this; }
        IRingBufferAutoScaleBuilder<T> IRingBufferAutoScaleBuilder<T>.OnError(Action<ILogger?, Exception> errorHandler) { SetOnError(errorHandler); return this; }

        IRingBufferService<T> IRingBufferAutoScaleBuilder<T>.Build(CancellationToken cancellation) => BuildCore(cancellation);

        async Task<IRingBufferService<T>> IRingBufferAutoScaleBuilder<T>.BuildWarmupAsync(CancellationToken cancellation)
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
                AutoScaleFault = _autoScaleFault,
                NumberFault = _numberFault,
                AcquireTimeout = _acquireTimeout,
                LockWhenScaling = _lockWhenScaling,
                ManualSwitchAllowed = _elastic && !_autoScaleFault,
                Logger = _logger,
                BackgroundLogger = _backgroundLogger,
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
                    var err = new InvalidOperationException("The min capacity is greater than the initial capacity.");
                    LogError(err);
                    throw err;
                }
                if (_maxCapacity < _initcapacity)
                {
                    var err = new InvalidOperationException("The max capacity is less than the initial capacity.");
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
            }
        }

        #endregion

        private void LogMessage(string message)
        {
            if (_logger is null || !_logger.IsEnabled(LogLevel.Debug)) return;

            SafeInvokeSink(() => logMessageForDbg(_logger, _uniqueName, message, null));
        }

        private void LogError(Exception message)
        {
            if (_logger is null || !_logger.IsEnabled(LogLevel.Error)) return;

            if (_errorHandler == null)
            {
                SafeInvokeSink(() => logMessageForErr(_logger, _uniqueName, message.ToString(), null));
            }
            else
            {
                SafeInvokeSink(() => _errorHandler?.Invoke(_logger, message));
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
