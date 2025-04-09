// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed class RingBufferBuilder<T> : IRingBuffer<T>, IRingBufferScaleCapacity<T>
    {
        #region Fields

        private readonly string _uniqueName;
        private ILogger? _logger;
        private int _initcapacity;
        private int _minCapacity;
        private int _maxCapacity;
        private int _sampleUnit;
        private int? _scaledownInit;
        private int? _scaledownMin;
        private int? _scaledownMax;
        private bool _triggerfault;
        private byte _numberfault;
        private bool _backgroundLogger;
        private bool _lockAcquire;


        private TimeSpan _samplebasetime;
        private TimeSpan _factoryTimeout;
        private TimeSpan _pulseHeartBeat;
        private TimeSpan _acquireTimeout;

        private TimeSpan _acquireDelayAttempts;
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
            _acquireDelayAttempts = RingBufferDefault.AcquireDelayAttempts;
        }

        #endregion

        public IRingBufferScaleCapacity<T> MaxCapacity(int value)
        {
            _maxCapacity = value;
            return this;
        }

        public IRingBufferScaleCapacity<T> MinCapacity(int value)
        {
            _minCapacity = value;
            return this;
        }

        public IRingBuffer<T> OnError(Action<ILogger?, Exception> errorHandler)
        {
            _errorHandler = errorHandler;
            return this;
        }

        public IRingBufferScaleCapacity<T> AutoScaleAcquireFault(byte numberfault = 1)
        {
            _triggerfault = true;
            _numberfault = numberfault;
            return this;
        }

        public IRingBuffer<T> AcquireTimeout(TimeSpan value, TimeSpan? delayattempts = null)
        {
            _acquireTimeout = value;
            _acquireDelayAttempts = delayattempts ?? RingBufferDefault.AcquireDelayAttempts;
            return this;
        }

        public IRingBuffer<T> HeartBeat(Action<RingBufferValue<T>> value, TimeSpan? pulse = null)
        {
            _bufferHeartBeat = value;
            _pulseHeartBeat = pulse ?? RingBufferDefault.PulseHeartBeat;
            return this;
        }

        public IRingBuffer<T> Capacity(int value)
        {
            _initcapacity = _maxCapacity = _minCapacity = value;
            return this;
        }

        public IRingBuffer<T> Factory(Func<CancellationToken, Task<T>> value, TimeSpan? timeout = null)
        {
            _factory = value;
            _factoryTimeout = timeout ?? RingBufferDefault.FactoryTimeout;
            return this;
        }

        public IRingBuffer<T> Logger(ILogger? value)
        {
            _logger = value;
            return this;
        }

        public IRingBuffer<T> BackgroundLogger(bool value = true)
        {
            _backgroundLogger = value;
            return this;
        }

        public IRingBufferScaleCapacity<T> LockWhenScaling(bool value = true)
        {
            _lockAcquire = value;
            return this;
        }

        public IRingBufferScaleCapacity<T> ScaleTimer(int? numberSamples = null, TimeSpan? baseTimer = null)
        {
            _sampleUnit = numberSamples ?? RingBufferDefault.SampleUnit;
            _samplebasetime = baseTimer ?? RingBufferDefault.SamplesBaseTime;
            return this;
        }

        public IRingBufferService<T> Build(CancellationToken token = default)
        {
            ValidateBuild();
            LogMessage("Build successfully and Created RingBuffer Manager");
            return new RingBufferManager<T>(token)
            {
                Name = _uniqueName,
                Capacity = _initcapacity,
                MinCapacity = _minCapacity,
                MaxCapacity = _maxCapacity,
                FactoryTimeout = _factoryTimeout,
                PulseHeartBeat = _pulseHeartBeat,
                SamplesBase = _samplebasetime,
                SamplesCount = _sampleUnit,
                ScaleDownInit = _scaledownInit,
                ScaleDownMin = _scaledownMin,
                ScaleDownMax = _scaledownMax,
                TriggerFault = _triggerfault,
                NumberFault = _numberfault,
                AcquireTimeout = _acquireTimeout,
                AcquireDelayAttempts = _acquireDelayAttempts,
                LockAcquire = _lockAcquire,
                Logger = _logger,
                BackgroundLogger = _backgroundLogger,
                ErrorHandler = _errorHandler,
                BufferHeartBeat = _bufferHeartBeat,
                Factory = _factory!
            };
        }

        public async Task<IRingBufferService<T>> BuildWarmupAsync(CancellationToken token = default)
        {
            var srv = Build(token);
            await srv.WarmupAsync(token);
            return srv;
        }

        private void ValidateBuild()
        {
            if (_factory is null)
            {
                var err = new InvalidOperationException("The command SetFactory is not defined.");
                LogError(err);
                throw err;
            }
            if (_initcapacity < 2)
            {
                var err = new InvalidOperationException("The capacity is less than 2.");
                LogError(err);
                throw err;
            }
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
                var err = new IndexOutOfRangeException("numberSamples in command SetScaleUnit must be greater or equal 1");
                LogError(err);
                throw err;
            }
            if (_samplebasetime.TotalMilliseconds / _sampleUnit < 100)
            {
                var err = new IndexOutOfRangeException("baseTimer / numberSamples in command SetScaleUnit must be greater or equal 100ms");
                LogError(err);
                throw err;
            }
            if (_triggerfault)
            {
                var localmin = _minCapacity - 2;
                if (localmin < 1)
                {
                    localmin = 1;
                }
                _scaledownInit = _initcapacity - _minCapacity + 2;
                _scaledownMin = localmin;
                _scaledownMax = _maxCapacity - _initcapacity + 2;
            }
            else
            {
                _scaledownInit = null;
                _scaledownMin = null;
                _scaledownMax = null;
            }
        }

        private void LogMessage(string message)
        {
            if (_logger is null || !_logger.IsEnabled(LogLevel.Debug)) return;

            logMessageForDbg(_logger, _uniqueName, message, null);
        }

        private void LogError(Exception message)
        {
            if (_logger is null || !_logger.IsEnabled(LogLevel.Error)) return;

            if (_errorHandler == null)
            {
                logMessageForErr(_logger, _uniqueName, message.ToString(), null);
            }
            else
            {
                _errorHandler?.Invoke(_logger, message);
            }
        }


        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferBuilder({source}) : {message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferBuilder({source}) : {message}");
    }
}
