using Microsoft.Extensions.Logging;
using RingBufferPlus.Events;
using RingBufferPlus.ObjectValues;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus
{
    public interface IPropertiesRingBuffer
    {
        string Alias { get; }
        RingBufferState CurrentState { get; }
        int InitialCapacity { get; }
        int MinimumCapacity { get; }
        int MaximumCapacity { get; }
        TimeSpan IntervalHealthCheck { get; }
        TimeSpan IntervalAutoScaler { get; }
        TimeSpan IntervalReport { get; }
        TimeSpan TimeoutAccquire { get; }
        TimeSpan IdleAccquire { get; }
        TimeSpan IntervalFailureState { get; }
        RingBufferPolicyTimeout PolicyTimeout { get; }
        LogLevel DefaultLogLevel { get; }
        bool HasLogging { get; }
        bool HasReport { get; }
        bool HasPolicyTimeout { get; }
        bool HasHealthCheck { get; }
        bool HasAutoScaler { get; }
        bool HasLinkedFailureState { get; }

    }

    public interface IRunningRingBuffer<T> : IPropertiesRingBuffer, IDisposable
    {
        RingBufferValue<T> Accquire(TimeSpan? timeout = null, CancellationToken? cancellation = null);
    }

    public interface IBuildRingBuffer<T> : IRunningRingBuffer<T>
    {
        event EventHandler<RingBufferErrorEventArgs>? ErrorCallBack;
        event EventHandler<RingBufferTimeoutEventArgs>? TimeoutCallBack;
        event EventHandler<RingBufferAutoScaleEventArgs>? AutoScalerCallback;
        IRunningRingBuffer<T> Run(CancellationToken? cancellationToken = null);
    }

    public interface IRingBuffer<T>
    {
        IRingBuffer<T> AliasName(string value);
        IRingBuffer<T> InitialBuffer(int value);
        IRingBuffer<T> MinBuffer(int value);
        IRingBuffer<T> MaxBuffer(int value);
        IRingBuffer<T> LinkedFailureState(Func<bool> value);
        IRingBuffer<T> SetPolicyTimeout(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null);
        IRingBuffer<T> SetTimeoutAccquire(TimeSpan value, TimeSpan? idle = null);
        IRingBuffer<T> SetTimeoutAccquire(long mileseconds, long? idle = null);
        IRingBuffer<T> SetIntervalHealthCheck(TimeSpan value);
        IRingBuffer<T> SetIntervalHealthCheck(long mileseconds);
        IRingBuffer<T> SetIntervalAutoScaler(TimeSpan value,TimeSpan ? warmup = null);
        IRingBuffer<T> SetIntervalAutoScaler(long mileseconds, long? warmup = null);
        IRingBuffer<T> SetIntervalFailureState(long mileseconds);
        IRingBuffer<T> SetIntervalFailureState(TimeSpan value);
        IRingBuffer<T> SetIntervalReport(TimeSpan value);
        IRingBuffer<T> SetIntervalReport(long mileseconds);
        IRingBuffer<T> Factory(Func<CancellationToken, T> value);
        IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value);
        IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value);
        IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value);
        IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> autoscaler);
        IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> autoscaler);
        IRingBuffer<T> MetricsReport(Action<RingBufferMetric, CancellationToken> report);
        IRingBuffer<T> MetricsReportAsync(Func<RingBufferMetric, CancellationToken, Task> report);
        IRingBuffer<T> AddLogProvider(ILoggerFactory value, RingBufferLogLevel defaultlevel = RingBufferLogLevel.Trace);
        IBuildRingBuffer<T> Build();
    }
}
