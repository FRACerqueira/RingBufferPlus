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
        TimeSpan WaitNextTry { get; }
        TimeSpan IntervalHealthCheck { get; }
        TimeSpan IntervalAutoScaler { get; }
        TimeSpan IntervalReport { get; }
        TimeSpan TimeoutAccquire { get; }
        TimeSpan IntervalOpenCircuit { get; }

        RingBufferPolicyTimeout PolicyTimeout { get; }
        bool HasLogging { get; }
        LogLevel DefaultLogLevel { get; }
        bool HasReport { get; }
        bool HasUserpolicyAccquire { get; }
        bool HasUserHealthCheck { get; }
        bool HasUserAutoScaler { get; }
        bool HasLinkedFailureState { get; }

    }

    public interface IRunningRingBuffer<T> : IPropertiesRingBuffer, IDisposable
    {
        RingBufferValue<T> Accquire(TimeSpan? timeout = null);
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
        IRingBuffer<T> PolicyTimeoutAccquire(RingBufferPolicyTimeout policy, Func<RingBufferMetric, CancellationToken, bool>? userpolicy = null);
        IRingBuffer<T> DefaultTimeoutAccquire(TimeSpan value);
        IRingBuffer<T> DefaultTimeoutAccquire(long mileseconds);
        IRingBuffer<T> DefaultIntervalHealthCheck(TimeSpan value);
        IRingBuffer<T> DefaultIntervalHealthCheck(long mileseconds);
        IRingBuffer<T> DefaultIntervalAutoScaler(TimeSpan value);
        IRingBuffer<T> DefaultIntervalAutoScaler(long mileseconds);
        IRingBuffer<T> DefaultIntervalOpenCircuit(long mileseconds);
        IRingBuffer<T> DefaultIntervalOpenCircuit(TimeSpan value);

        IRingBuffer<T> DefaultIntervalReport(TimeSpan value);
        IRingBuffer<T> DefaultIntervalReport(long mileseconds);
        IRingBuffer<T> Factory(Func<CancellationToken, T> value);
        IRingBuffer<T> FactoryAsync(Func<CancellationToken, Task<T>> value);
        IRingBuffer<T> HealthCheck(Func<T, CancellationToken, bool> value);
        IRingBuffer<T> HealthCheckAsync(Func<T, CancellationToken, Task<bool>> value);
        IRingBuffer<T> AutoScaler(Func<RingBufferMetric, CancellationToken, int> autoscaler);
        IRingBuffer<T> AutoScalerAsync(Func<RingBufferMetric, CancellationToken, Task<int>> autoscaler);
        IRingBuffer<T> MetricsReport(Action<RingBufferMetric, CancellationToken> report);
        IRingBuffer<T> MetricsReportAsync(Func<RingBufferMetric, CancellationToken, Task> report);
        IRingBuffer<T> AddLogProvider(RingBufferLogLevel defaultlevel, ILoggerFactory value);
        IBuildRingBuffer<T> Build();
    }
}
