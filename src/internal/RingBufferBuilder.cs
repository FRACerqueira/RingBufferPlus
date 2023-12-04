// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{

    internal class RingBufferBuilder<T>(string uniquename, ILoggerFactory? loggerFactory, CancellationToken? cancellation) : IRingBuffer<T>,IRingBufferScaleCapacity<T>, IRingBufferScaleMax<T>, IRingBufferScaleMin<T>, IRingBufferOptions<T>
    {
        private readonly ILoggerFactory? _loggerFactory = loggerFactory;
        private readonly CancellationToken _apptoken = cancellation??CancellationToken.None;

        #region IRingBufferOptions

        public bool IsSlave { get; private set; }

        public string Name => uniquename;

        public bool HasScaleCallbackEvent { get; private set; }

        public int Capacity { get; private set; } = 2;

        public int MinCapacity { get; private set; } = 2;

        public int MaxCapacity { get; private set; } = 2;

        public Func<CancellationToken, T> FactoryHandler { get; private set; }

        public Func<T,bool> BufferHealthHandler { get; private set; }
        public TimeSpan FactoryTimeout { get; private set; } = TimeSpan.FromSeconds(10);

        public TimeSpan BufferHealtTimeout { get; private set; } = TimeSpan.FromSeconds(30);

        public TimeSpan FactoryIdleRetryError { get; private set; } = TimeSpan.FromSeconds(5);

        public ILogger Logger { get; private set; }

        public bool HasScaleCapacity { get; private set; }

        public Action<ILogger, RingBufferException> ErrorHandler { get; private set; }

        public TimeSpan SampleDelay => TimeSpan.FromMilliseconds(ScaleCapacityDelay.TotalMilliseconds / SampleUnit);

        public int SampleUnit { get; private set; } = 60;

        public TimeSpan ScaleCapacityDelay { get; private set; } = TimeSpan.FromSeconds(60);

        public int? ScaleToMinGreaterEq { get; private set; }

        public int? MinRollbackWhenFreeLessEq { get; private set; }

        public int? ScaleToMaxLessEq { get; private set; }

        public int? MaxRollbackWhenFreeGreaterEq { get; private set; }

        public int? MinTriggerByAccqWhenFreeGreaterEq  { get; private set; }

        public int? MaxTriggerByAccqWhenFreeGreaterEq { get; private set; }

        public Action<RingBufferMetric, ILogger?, CancellationToken?> ReportHandler { get; private set; }

        public TimeSpan AccquireTimeout { get; private set; } = TimeSpan.FromSeconds(30);

        public IRingBufferSwith SwithFrom { get; private set; }

        public IRingBufferSwith SwithTo { get; private set; }

        public ScaleMode UserSwithScale { get; private set; } = ScaleMode.Automatic;


        #endregion

        #region IRingBuffer


        IRingBuffer<T> IRingBuffer<T>.Logger(ILogger value)
        {
            Logger = value;
            return this;
        }


        IRingBufferService<T> IRingBuffer<T>.BuildWarmup(out bool fullcapacity, TimeSpan? timeout)
        {
            return SharedBuildWarmup(out fullcapacity, timeout);
        }

        IRingBuffer<T> IRingBuffer<T>.OnError(Action<ILogger?, RingBufferException> errorhandler)
        {
#if NETSTANDARD2_1
            if (errorhandler is null)
            {
                throw new ArgumentNullException(nameof(errorhandler));
            }
#else
            ArgumentNullException.ThrowIfNull(errorhandler);
#endif
            ErrorHandler = errorhandler;
            return this;
        }

        IRingBuffer<T> IRingBuffer<T>.AccquireTimeout(TimeSpan value)
        {
            if (value.TotalMilliseconds < 1)
            {
                throw new ArgumentException("Timespan must be greater than or equal to 1 milliseconds", nameof(value));
            }
            AccquireTimeout = value;
            return this;
        }

        IRingBuffer<T> IRingBuffer<T>.Capacity(int value)
        {
            if (value < 2)
            {
                throw new ArgumentException("Capacity must be greater than or equal to 2", nameof(value));
            }
            Capacity = value;
            MinCapacity = value;
            MaxCapacity = value;
            return this;
        }

        IRingBuffer<T> IRingBuffer<T>.BufferHealth(Func<T, bool> value, TimeSpan? timeout)
        {
#if NETSTANDARD2_1
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
#else
            ArgumentNullException.ThrowIfNull(value);
#endif
            BufferHealthHandler = value;
            BufferHealtTimeout = timeout ?? TimeSpan.FromSeconds(30);
            return this;
        }

        IRingBuffer<T> IRingBuffer<T>.Factory(Func<CancellationToken, T> value, TimeSpan? timeout, TimeSpan? idleRetryError)
        {
#if NETSTANDARD2_1
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
#else
            ArgumentNullException.ThrowIfNull(value);
#endif
            FactoryHandler = value;
            if (timeout.HasValue)
            {
                if (timeout.Value.TotalMilliseconds < 1)
                {
                    throw new ArgumentException("timeout must be greater than or equal to 1 milliseconds", nameof(value));
                }
                FactoryTimeout = timeout.Value;
            }
            if (idleRetryError.HasValue)
            {
                if (idleRetryError.Value.TotalMilliseconds < 1)
                {
                    throw new ArgumentException("idleRetryError must be greater than or equal to 1 milliseconds", nameof(value));
                }
                FactoryIdleRetryError = idleRetryError.Value;
            }
            return this;
        }

        IRingBufferScaleCapacity<T> IRingBuffer<T>.ScaleUnit(ScaleMode unit, int? value, TimeSpan? baseunit)
        {
            UserSwithScale = unit;
            IsSlave = false;
            switch (unit)
            {
                case ScaleMode.Automatic:
                    SharedSampleUnit(baseunit ?? TimeSpan.FromSeconds(60), value);
                    break;
                case ScaleMode.Manual:
                    SharedSampleUnit(TimeSpan.FromSeconds(60), 60);
                    break;
                case ScaleMode.Slave:
                    SwithTo = null;
                    SwithFrom = null;
                    IsSlave = true;
                    break;
                default:
                    throw new NotImplementedException($"ScaleUnit {unit}");
            }
            return this;
        }

        IRingBufferService<T> IRingBuffer<T>.Build()
        {
            return SharedBuild();
        }

        #endregion

        #region IRingBufferScaleCapacity

        IRingBufferScaleCapacity<T> IRingBufferScaleCapacity<T>.Slave(IRingBufferPlus slaveRingBuffer)
        {
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"Slave do not use with Slave scale");
            }
            IsSlave = false;
            SwithTo = (IRingBufferSwith)slaveRingBuffer;
            SwithFrom = null;
            return this;
        }

        IRingBufferService<T> IRingBufferScaleCapacity<T>.BuildWarmup(out bool fullcapacity, TimeSpan? timeout)
        {
            return SharedBuildWarmup(out fullcapacity, timeout);
        }

        IRingBufferService<T> IRingBufferScaleCapacity<T>.Build()
        {
            return SharedBuild();
        }

        IRingBufferScaleMax<T> IRingBufferScaleCapacity<T>.MaxCapacity(int value)
        {
            ShareMaxCapacity(value);
            MaxTriggerByAccqWhenFreeGreaterEq = null;
            return this;
        }

        IRingBufferScaleMin<T> IRingBufferScaleCapacity<T>.MinCapacity(int value)
        {
            ShareMinCapacity(value);
            MinTriggerByAccqWhenFreeGreaterEq = null;
            return this;
        }


        IRingBufferScaleCapacity<T> IRingBufferScaleCapacity<T>.ReportScale(Action<RingBufferMetric, ILogger?, CancellationToken?> report)
        {
            SharedReport(report);
            return this;
        }

        #endregion

        #region IRingBufferScaleMax

        IRingBufferScaleMax<T> IRingBufferScaleMax<T>.TriggerByAccqWhenFreeGreaterEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"TriggerAccqGreaterEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"TriggerAccqGreaterEq do not use with Slave scale");
            }
            var localvalue = value ?? MaxCapacity - Capacity;
            if (localvalue < MaxCapacity - Capacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be greater or equal({ScaleToMaxLessEq})");
            }
            if (localvalue > MaxCapacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less or equal({MaxCapacity})");
            }
            if (MaxRollbackWhenFreeGreaterEq >= 0)
            {
                throw new InvalidOperationException($"TriggerAccqGreaterEq do not use with RollbackGreaterEq");
            }
            MaxTriggerByAccqWhenFreeGreaterEq = localvalue;
            MaxRollbackWhenFreeGreaterEq = null;
            return this;
        }

        IRingBufferScaleMax<T> IRingBufferScaleMax<T>.ScaleWhenFreeLessEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"ScaleWhenFreeLessEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"ScaleWhenFreeLessEq do not use with Slave scale");
            }
            var localvalue = value ?? 2;
            if (localvalue < 2)
            {
                throw new ArgumentException($"The value({localvalue}) must be greater or equal 2");
            }
            if (localvalue > Capacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less or equal({Capacity})");
            }
            ScaleToMaxLessEq = localvalue;
            return this;
        }

        IRingBufferScaleMax<T> IRingBufferScaleMax<T>.RollbackWhenFreeGreaterEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"RollbackWhenFreeGreaterEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"RollbackWhenFreeGreaterEq do not use with Slave scale");
            }
            var localvalue = value ?? MaxCapacity - Capacity;
            if (localvalue < MaxCapacity - Capacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be greater or equal({ScaleToMaxLessEq})");
            }
            if (localvalue > MaxCapacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less or equal({MaxCapacity})");
            }
            if (MaxTriggerByAccqWhenFreeGreaterEq >= 0)
            {
                throw new InvalidOperationException($"RollbackGreaterEq do not use with TriggerAccqGreaterEq");
            }
            MaxRollbackWhenFreeGreaterEq = localvalue;
            MaxTriggerByAccqWhenFreeGreaterEq = null;
            return this;
        }


        IRingBufferScaleMin<T> IRingBufferScaleMax<T>.MinCapacity(int value)
        {
            ShareMinCapacity(value);
            MaxTriggerByAccqWhenFreeGreaterEq = null;
            return this;

        }

        IRingBufferService<T> IRingBufferScaleMax<T>.Build()
        {
            return SharedBuild();
        }

        IRingBufferService<T> IRingBufferScaleMax<T>.BuildWarmup(out bool fullcapacity, TimeSpan? timeout)
        {
            return SharedBuildWarmup(out fullcapacity, timeout);
        }


        #endregion

        #region IRingBufferScaleMin

        IRingBufferScaleMin<T> IRingBufferScaleMin<T>.RollbackWhenFreeLessEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"RollbackWhenFreeLessEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"RollbackWhenFreeLessEq do not use with Slave scale");
            }
            var localvalue = value ?? 1;
            if (localvalue < 1)
            {
                throw new ArgumentException($"The value({localvalue}) must be greater than or equal to 1", nameof(value));
            }
            if (localvalue > MinCapacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less or equal({MinCapacity}) than to MinCapacity({MinCapacity})", nameof(value));
            }
            if (MinTriggerByAccqWhenFreeGreaterEq  >= 0)
            {
                throw new InvalidOperationException($"RollbackWhenFreeGreaterEq do not use with TriggerByAccqWhenFreeGreaterEq");
            }
            MinRollbackWhenFreeLessEq = localvalue;
            MinTriggerByAccqWhenFreeGreaterEq = null;
            return this;
        }

        IRingBufferScaleMin<T> IRingBufferScaleMin<T>.ScaleWhenFreeGreaterEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"ScaleWhenFreeGreaterEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"ScaleWhenFreeGreaterEq do not use with Slave scale");
            }
            var localvalue = value ?? Capacity;
            if (localvalue < 2)
            {
                throw new ArgumentException($"The value({localvalue}) must be greater than or equal to 2", nameof(value));
            }
            if (localvalue > Capacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less or equal({Capacity-1}) to Capacity({Capacity})-1", nameof(value));
            }
            ScaleToMinGreaterEq = localvalue;
            MinTriggerByAccqWhenFreeGreaterEq  = null;
            return this;
        }

        IRingBufferScaleMin<T> IRingBufferScaleMin<T>.TriggerByAccqWhenFreeLessEq(int? value)
        {
            if (UserSwithScale == ScaleMode.Manual)
            {
                throw new InvalidOperationException($"TriggerByAccqWhenFreeLessEq do not use with Manual scale");
            }
            if (UserSwithScale == ScaleMode.Slave)
            {
                throw new InvalidOperationException($"TriggerByAccqWhenFreeLessEq do not use with Slave scale");
            }
            var localvalue = value ?? MinCapacity;
            if (localvalue < 2)
            {
                throw new ArgumentException($"The value must be greater than or equal to 2", nameof(value));
            }
            if (localvalue > MinCapacity)
            {
                throw new ArgumentException($"The value({localvalue}) must be less than to MinCapacity({MinCapacity})", nameof(value));
            }
            if (MinRollbackWhenFreeLessEq >= 0)
            {
                throw new InvalidOperationException($"TriggerByAccqWhenFreeGreaterEq do not use with RollbackWhenFreeGreaterEq");
            }
            MinRollbackWhenFreeLessEq = null;
            MinTriggerByAccqWhenFreeGreaterEq  = localvalue;
            return this;
        }

        IRingBufferScaleMax<T> IRingBufferScaleMin<T>.MaxCapacity(int value)
        {
            ShareMaxCapacity(value);
            MaxTriggerByAccqWhenFreeGreaterEq = null;
            return this;
        }

        IRingBufferService<T> IRingBufferScaleMin<T>.Build()
        {
            return SharedBuild();
        }

        IRingBufferService<T> IRingBufferScaleMin<T>.BuildWarmup(out bool fullcapacity, TimeSpan? timeout)
        {
            return SharedBuildWarmup(out fullcapacity, timeout);
        }

        #endregion

        private void SharedSampleUnit(TimeSpan? baseunit, int? value)
        {
            var localvalue = value ?? 60;
            var localbase = baseunit ?? TimeSpan.FromMilliseconds(60);
            if (localvalue < 1)
            {
                throw new IndexOutOfRangeException("Value must be greater or equal 1");
            }
            if (localbase.TotalMilliseconds / localvalue < 100)
            {
                throw new IndexOutOfRangeException("baseunit/Value must be greater or equal 100ms");
            }
            ScaleCapacityDelay = localbase;
            SampleUnit = localvalue;
        }

        private void ShareMaxCapacity(int value)
        {
            if (value < Capacity)
            {
                throw new ArgumentException($"MaxCapacity must be greater than or equal to Capacity({Capacity})", nameof(value));
            }
            MaxCapacity = value;
            if (UserSwithScale == ScaleMode.Automatic && !IsSlave)
            {
                if (!ScaleToMaxLessEq.HasValue)
                {
                    ScaleToMaxLessEq = 2;
                }
                if (!MaxRollbackWhenFreeGreaterEq.HasValue)
                {
                    MaxRollbackWhenFreeGreaterEq = MaxCapacity - Capacity;
                }
            }
        }

        private void ShareMinCapacity(int value)
        {
            if (value < 1)
            {
                throw new ArgumentException("MinCapacity must be greater than or equal to 1", nameof(value));
            }
            if (value > Capacity)
            {
                throw new ArgumentException($"MinCapacity must be less than or equal to Capacity({Capacity})", nameof(value));
            }
            MinCapacity = value;
            if (UserSwithScale == ScaleMode.Automatic && !IsSlave)
            {
                if (!ScaleToMinGreaterEq.HasValue)
                {
                    ScaleToMinGreaterEq = Capacity;
                }
                if (!MinRollbackWhenFreeLessEq.HasValue)
                {
                    MinRollbackWhenFreeLessEq = 1;
                }
            }
        }

        private void SharedReport(Action<RingBufferMetric, ILogger?, CancellationToken?> report)
        {
#if NETSTANDARD2_1
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }
#else
            ArgumentNullException.ThrowIfNull(report);
#endif            
            ReportHandler = report;
        }

        private RingBufferManager<T> SharedBuild()
        {
            if (_loggerFactory is not null && Logger is null)
            {
                Logger = _loggerFactory.CreateLogger(Name);
            }
            if (FactoryHandler is null)
            {
                throw new RingBufferException(Name, "Factory Handler is null");
            }
            if (MaxCapacity != Capacity)
            {
                if (SwithTo != null)
                {
                    if (!ScaleToMaxLessEq.HasValue)
                    {
                        throw new RingBufferException(Name, "MaxCapacity : ScaleWhenFreeLessEq is null");
                    }
                    if (!MaxTriggerByAccqWhenFreeGreaterEq.HasValue && !MaxRollbackWhenFreeGreaterEq.HasValue)
                    {
                        throw new RingBufferException(Name, "MaxCapacity : TriggerByAccqWhenFreeGreaterEq or RollbackWhenFreeGreaterEq is null");
                    }
                    if (!HasScaleCapacity)
                    {
                        HasScaleCapacity = MaxRollbackWhenFreeGreaterEq.HasValue;
                    }
                }
            }
            if (MinCapacity != Capacity)
            { 
                if (SwithTo != null)
                {
                    if (!ScaleToMinGreaterEq.HasValue)
                    {
                        throw new RingBufferException(Name, "MaxCapacity : ScaleWhenFreeGreaterEq is null");
                    }
                    if (!MinTriggerByAccqWhenFreeGreaterEq.HasValue && !MinRollbackWhenFreeLessEq.HasValue)
                    {
                        throw new RingBufferException(Name, "MinCapacity: TriggerByAccqWhenFreeGreaterEq or  RollbackWhenFreeGreaterEq is null");
                    }
                    if (!HasScaleCapacity)
                    {
                        HasScaleCapacity = MinRollbackWhenFreeLessEq.HasValue;
                    }
                }
            }
            return new RingBufferManager<T>(this, _apptoken);
        }

        private RingBufferManager<T> SharedBuildWarmup(out bool fullcapacity, TimeSpan? timeout)
        {
            var ctrl = SharedBuild();
            fullcapacity = ctrl.Warmup(timeout ?? TimeSpan.FromSeconds(30));
            return ctrl;
        }
    }
}
