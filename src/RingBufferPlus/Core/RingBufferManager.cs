// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

// Design note (ADR001/ADR005): all mutable scale state (_currentCapacity, fault counters,
// samples) is owned exclusively by the single consumer loop (RunEngineAsync). No other thread
// ever mutates it, so no lock/semaphore is needed for correctness. Callers only ever post
// commands into an unbounded Channel<EngineCommand> and, optionally, await a completion signal.
//
// Two deliberate simplifications versus v4, both authorized by ADR006 (no compatibility
// obligation) and consistent with ADR001's "correctness by construction over blocking dances":
//  - AcquireAsync never blocks waiting for an in-flight scale operation. It always reads
//    directly from the available-items channel (which blocks only until an item exists),
//    regardless of LockWhenScaling. LockWhenScaling now controls exactly one thing: whether
//    SwitchToAsync's caller awaits the scale operation's completion before returning.
//  - AcquireDelayAttempts is removed: a Channel-based read has no polling loop to pace.
//  - Warmup runs at most once per instance (Lazy<Task>, ExecutionAndPublication) and caches a
//    failure: an instance whose warmup throws is permanently broken by design; construct a new
//    instance to retry (v4's retry path was itself broken: a failed Startup() left
//    _WarmupRunning stuck true forever).

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed class RingBufferManager<T> : IRingBufferManualScaleService<T>
    {
        #region fields

        private readonly CancellationTokenSource _lifetime;
        private readonly Channel<T> _availableItems = Channel.CreateUnbounded<T>();
        private readonly Channel<EngineCommand> _commands = Channel.CreateUnbounded<EngineCommand>();
        private readonly Channel<LogMessageBackground> _logQueue = Channel.CreateUnbounded<LogMessageBackground>();
        private readonly Lazy<Task> _warmup;
        private readonly Task _engineTask;
        private readonly List<int> _samples = [];

        private Task? _heartbeatTask;
        private Task? _sampleTickTask;
        private Task? _loggerTask;

        private bool _disposed;
        private int _currentCapacity;
        private volatile bool _scaling;
        private int _faultCount;

        #endregion

        #region configuration (set by RingBufferBuilder via object initializer)

        public required string Name { get; init; }

        public int Capacity { get; init; }

        public int MinCapacity { get; init; }

        public int MaxCapacity { get; init; }

        public TimeSpan FactoryTimeout { get; init; }

        public TimeSpan PulseHeartBeat { get; init; }

        public TimeSpan SamplesBase { get; init; }

        public int SamplesCount { get; init; }

        public int? ScaleDownInit { get; init; }

        public int? ScaleDownMin { get; init; }

        public int? ScaleDownMax { get; init; }

        public bool AutoScaleFault { get; init; }

        public byte NumberFault { get; init; }

        public TimeSpan AcquireTimeout { get; init; }

        public bool LockWhenScaling { get; init; }

        /// <summary>
        /// True only for elastic buffers without autoscale-on-fault. Guards the escaped-cast path:
        /// <see cref="SwitchToAsync(ScaleSwitch)"/> is not exposed at the type level otherwise (ADR007),
        /// but a caller that casts back to <see cref="IRingBufferManualScaleService{T}"/> must not silently no-op.
        /// </summary>
        public bool ManualSwitchAllowed { get; init; }

        public ILogger? Logger { get; init; }

        public bool BackgroundLogger { get; init; }

        public Action<ILogger?, Exception>? ErrorHandler { get; init; }

        public Action<RingBufferValue<T>>? BufferHeartBeat { get; init; }

        public required Func<CancellationToken, Task<T>> Factory { get; init; }

        #endregion

        #region IRingBufferService

        public bool IsMinCapacity => CurrentCapacity == MinCapacity;

        public bool IsMaxCapacity => CurrentCapacity == MaxCapacity;

        public bool IsInitCapacity => CurrentCapacity == Capacity;

        public int CurrentCapacity => Volatile.Read(ref _currentCapacity);

        #endregion

        public RingBufferManager(CancellationToken lifetimecancellation)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimecancellation);
            _warmup = new Lazy<Task>(WarmupCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
            _engineTask = Task.Run(RunEngineAsync);
        }

        public async ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureWarmupAsync().ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource(AcquireTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _lifetime.Token, cancellation);
            try
            {
                var item = await _availableItems.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                return new RingBufferValue<T>(Name, sw.Elapsed, true, item, TurnbackAsync);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    LogWarning("RingBuffer without resource");
                    if (AutoScaleFault)
                    {
                        _commands.Writer.TryWrite(EngineCommand.Fault());
                    }
                }
                return new RingBufferValue<T>(Name, sw.Elapsed, false, default!, null);
            }
        }

        public async Task<bool> SwitchToAsync(ScaleSwitch value)
        {
            if (!ManualSwitchAllowed)
            {
                throw new InvalidOperationException("Manual scale switching is not available: the buffer has a fixed capacity, or autoscale-on-fault is enabled (see ADR007).");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                await EnsureWarmupAsync().ConfigureAwait(false);

                var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Switch(value, accepted, completion), _lifetime.Token).ConfigureAwait(false);

                var wasAccepted = await accepted.Task.ConfigureAwait(false);
                if (!wasAccepted)
                {
                    return false;
                }
                return !LockWhenScaling || await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public Task WarmupAsync(CancellationToken cancellation = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _warmup.Value.WaitAsync(cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _lifetime.CancelAsync().ConfigureAwait(false);
            _commands.Writer.TryComplete();
            _logQueue.Writer.TryComplete();

            // If a warmup was already in flight, let it unwind first (it observes cancellation
            // and returns or throws) before snapshotting which background pumps to await -
            // otherwise a pump task WarmupCoreAsync assigns after our snapshot would never be
            // awaited below.
            if (_warmup.IsValueCreated)
            {
                try
                {
                    await _warmup.Value.ConfigureAwait(false);
                }
                catch
                {
                    //ignore: disposal is in progress, warmup's own outcome no longer matters
                }
            }

            var pending = new List<Task> { _engineTask };
            if (_heartbeatTask is not null) pending.Add(_heartbeatTask);
            if (_sampleTickTask is not null) pending.Add(_sampleTickTask);
            if (_loggerTask is not null) pending.Add(_loggerTask);

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                //ignore: expected once _lifetime is cancelled
            }

            _availableItems.Writer.TryComplete();
            while (_availableItems.Reader.TryRead(out var item))
            {
                await DisposeItemAsync(item).ConfigureAwait(false);
            }

            _lifetime.Dispose();
        }

        private Task EnsureWarmupAsync() => _warmup.Value;

        private async Task WarmupCoreAsync()
        {
            if (!_disposed && BackgroundLogger && (Logger is not null || ErrorHandler is not null))
            {
                _loggerTask = Task.Run(RunLoggerAsync);
            }

            LogMessage("Starting warmup process.");

            bool reached;
            try
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _commands.Writer.WriteAsync(EngineCommand.Warmup(completion), _lifetime.Token).ConfigureAwait(false);
                reached = await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reached = false;
            }

            if (!reached)
            {
                var err = new InvalidOperationException("RingBuffer did not reach initial capacity");
                LogError(err);
                throw err;
            }

            LogMessage($"End warmup process with {CurrentCapacity} buffers.");

            if (!_disposed && BufferHeartBeat is not null)
            {
                _heartbeatTask = Task.Run(RunHeartbeatAsync);
            }
            if (!_disposed && AutoScaleFault)
            {
                _sampleTickTask = Task.Run(RunSampleTickAsync);
            }
        }

        private async ValueTask TurnbackAsync(RingBufferValue<T> value)
        {
            if (!value.Successful) return;
            try
            {
                if (!value.SkipTurnback)
                {
                    await _availableItems.Writer.WriteAsync(value.Current, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await DisposeItemAsync(value.Current).ConfigureAwait(false);
                    _commands.Writer.TryWrite(EngineCommand.ReplaceOne());
                }
            }
            catch (ChannelClosedException)
            {
                //ignore: manager disposed concurrently with turnback
            }
        }

        #region engine loop (single consumer of _commands; sole owner of _currentCapacity)

        private async Task RunEngineAsync()
        {
            try
            {
                await foreach (var cmd in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await ProcessCommandAsync(cmd).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
        }

        private async Task ProcessCommandAsync(EngineCommand cmd)
        {
            switch (cmd.Kind)
            {
                case EngineCommandKind.Warmup:
                    var reached = await MoveToCapacityAsync(Capacity, hasTimeout: false, _lifetime.Token).ConfigureAwait(false);
                    cmd.Completion?.TrySetResult(reached);
                    break;

                case EngineCommandKind.Switch:
                    var target = ResolveTarget(cmd.Target!.Value);
                    if (target == CurrentCapacity)
                    {
                        cmd.Accepted?.TrySetResult(false);
                        cmd.Completion?.TrySetResult(false);
                        break;
                    }
                    cmd.Accepted?.TrySetResult(true);
                    var moved = await MoveToCapacityAsync(target, hasTimeout: true, _lifetime.Token).ConfigureAwait(false);
                    cmd.Completion?.TrySetResult(moved);
                    break;

                case EngineCommandKind.Fault:
                    _faultCount++;
                    if (_faultCount > NumberFault && CurrentCapacity != MaxCapacity)
                    {
                        _faultCount = 0;
                        var next = CurrentCapacity == MinCapacity ? Capacity : MaxCapacity;
                        await MoveToCapacityAsync(next, hasTimeout: true, _lifetime.Token).ConfigureAwait(false);
                    }
                    break;

                case EngineCommandKind.ReplaceOne:
                    await CreateSingleReplacementAsync().ConfigureAwait(false);
                    break;

                case EngineCommandKind.Tick:
                    await ProcessTickAsync().ConfigureAwait(false);
                    break;
            }
        }

        private async Task ProcessTickAsync()
        {
            if (_scaling)
            {
                _samples.Clear();
                return;
            }
            _samples.Add(_availableItems.Reader.Count);
            if (_samples.Count < SamplesCount)
            {
                return;
            }
            var median = AutoScaleDecision.Median(_samples);
            _samples.Clear();
            var target = AutoScaleDecision.EvaluateScaleDown(median, IsInitCapacity, IsMaxCapacity, MinCapacity, Capacity, ScaleDownInit, ScaleDownMax);
            if (target.HasValue)
            {
                await MoveToCapacityAsync(target.Value, hasTimeout: true, _lifetime.Token).ConfigureAwait(false);
            }
        }

        private int ResolveTarget(ScaleSwitch value) => value switch
        {
            ScaleSwitch.MinCapacity => MinCapacity,
            ScaleSwitch.MaxCapacity => MaxCapacity,
            _ => Capacity
        };

        private async Task<bool> MoveToCapacityAsync(int target, bool hasTimeout, CancellationToken token)
        {
            var current = CurrentCapacity;
            if (target == current) return true;

            _scaling = true;
            try
            {
                if (target > current)
                {
                    var quantity = target - current;
                    LogMessage($"Starting ScaleUp {quantity}.");
                    var created = await CreateItemsAsync(quantity, hasTimeout, token).ConfigureAwait(false);
                    LogMessage("End ScaleUp.");
                    if (created != quantity) return false;
                    Volatile.Write(ref _currentCapacity, target);
                    return true;
                }
                else
                {
                    var quantity = current - target;
                    LogMessage($"Starting ScaleDown {quantity}.");
                    var removed = await RemoveItemsAsync(quantity, hasTimeout, token).ConfigureAwait(false);
                    LogMessage("End ScaleDown.");
                    if (removed != quantity) return false;
                    Volatile.Write(ref _currentCapacity, target);
                    return true;
                }
            }
            finally
            {
                _scaling = false;
            }
        }

        private async Task<int> CreateItemsAsync(int quantity, bool hasTimeout, CancellationToken token)
        {
            var created = new List<T>(quantity);
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (hasTimeout) overall.CancelAfter(SamplesBase);
            try
            {
                while (created.Count < quantity)
                {
                    using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    factoryTimeout.CancelAfter(FactoryTimeout);
                    try
                    {
                        var item = await Factory(overall.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                        created.Add(item);
                    }
                    catch (OperationCanceledException) when (factoryTimeout.IsCancellationRequested && !overall.IsCancellationRequested)
                    {
                        LogError(new TimeoutException("Timeout factory"));
                        throw;
                    }
                }
                foreach (var item in created)
                {
                    await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                }
                return created.Count;
            }
            catch (OperationCanceledException)
            {
                LogError(new TimeoutException($"Timeout ScaleUp {created.Count}/{quantity}."));
                foreach (var item in created)
                {
                    await DisposeItemAsync(item).ConfigureAwait(false);
                }
                return 0;
            }
        }

        private async Task<int> RemoveItemsAsync(int quantity, bool hasTimeout, CancellationToken token)
        {
            var removed = new List<T>(quantity);
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (hasTimeout) overall.CancelAfter(SamplesBase);
            try
            {
                while (removed.Count < quantity)
                {
                    var item = await _availableItems.Reader.ReadAsync(overall.Token).ConfigureAwait(false);
                    removed.Add(item);
                }
                foreach (var item in removed)
                {
                    await DisposeItemAsync(item).ConfigureAwait(false);
                }
                return removed.Count;
            }
            catch (OperationCanceledException)
            {
                LogError(new TimeoutException($"Timeout ScaleDown {removed.Count}/{quantity}."));
                foreach (var item in removed)
                {
                    await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                }
                return 0;
            }
        }

        private async Task CreateSingleReplacementAsync()
        {
            using var factoryTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            factoryTimeout.CancelAfter(FactoryTimeout);
            try
            {
                var item = await Factory(_lifetime.Token).WaitAsync(factoryTimeout.Token).ConfigureAwait(false);
                await _availableItems.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogError(new TimeoutException("Timeout factory (replacement)"));
            }
        }

        private static async ValueTask DisposeItemAsync(T item)
        {
            if (item is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (item is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        #endregion

        #region background pumps

        private async Task RunHeartbeatAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(PulseHeartBeat, _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    LogMessage("Started Heart Beat item");
                    var acquired = await AcquireAsync(_lifetime.Token).ConfigureAwait(false);
                    await using (acquired)
                    {
                        if (!acquired.Successful)
                        {
                            LogMessage("Heart Beat item not available");
                            continue;
                        }
                        using var pulseTimeout = new CancellationTokenSource(PulseHeartBeat);
                        try
                        {
                            await Task.Run(() => BufferHeartBeat?.Invoke(acquired), pulseTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            LogError(new TimeoutException("Timeout Heart Beat"));
                        }
                        catch (Exception ex)
                        {
                            LogError(ex);
                        }
                    }
                    LogMessage("Stopped Heart Beat item");
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
        }

        private async Task RunSampleTickAsync()
        {
            try
            {
                await Task.Delay(SamplesBase, _lifetime.Token).ConfigureAwait(false);
                var delay = TimeSpan.FromMilliseconds(SamplesBase.TotalMilliseconds / SamplesCount);
                while (!_lifetime.IsCancellationRequested)
                {
                    _commands.Writer.TryWrite(EngineCommand.Tick());
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                //ignore: manager disposed
            }
        }

        private async Task RunLoggerAsync()
        {
            try
            {
                await foreach (var item in _logQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(item.Message))
                    {
                        if (item.LogLevel == LogLevel.Debug)
                        {
                            logMessageForDbg(Logger!, Name, item.Message, null);
                        }
                        else if (item.LogLevel == LogLevel.Warning)
                        {
                            logMessageFoWrn(Logger!, Name, item.Message, null);
                        }
                    }
                    if (item.Error is not null)
                    {
                        if (ErrorHandler is null)
                        {
                            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {item.Error.Message} ";
                            logMessageForErr(Logger!, Name, msg, item.Error);
                        }
                        else
                        {
                            ErrorHandler.Invoke(Logger, item.Error);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //ignore
            }
        }

        #endregion

        #region logging

        private void LogMessage(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Debug, msg, null));
            }
            else
            {
                logMessageForDbg(Logger, Name, msg, null);
            }
        }

        private void LogWarning(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Warning, msg, null));
            }
            else
            {
                logMessageFoWrn(Logger, Name, msg, null);
            }
        }

        private void LogError(Exception error)
        {
            if (Logger is null && ErrorHandler is null) return;
            if (BackgroundLogger)
            {
                _logQueue.Writer.TryWrite(new LogMessageBackground(LogLevel.Error, null, error));
            }
            else if (ErrorHandler is null)
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {error.Message} ";
                logMessageForErr(Logger!, Name, msg, error);
            }
            else
            {
                ErrorHandler.Invoke(Logger, error);
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({source}) : {message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferManager({source}) : {message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageFoWrn = LoggerMessage.Define<string, string>(LogLevel.Warning, 0, "RingBufferManager({source}) : {message}");

        #endregion

        private enum EngineCommandKind { Warmup, Switch, Fault, ReplaceOne, Tick }

        private sealed record EngineCommand
        {
            public required EngineCommandKind Kind { get; init; }
            public ScaleSwitch? Target { get; init; }
            public TaskCompletionSource<bool>? Accepted { get; init; }
            public TaskCompletionSource<bool>? Completion { get; init; }

            public static EngineCommand Warmup(TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Warmup, Completion = completion };

            public static EngineCommand Switch(ScaleSwitch target, TaskCompletionSource<bool> accepted, TaskCompletionSource<bool> completion) =>
                new() { Kind = EngineCommandKind.Switch, Target = target, Accepted = accepted, Completion = completion };

            public static EngineCommand Fault() => new() { Kind = EngineCommandKind.Fault };

            public static EngineCommand ReplaceOne() => new() { Kind = EngineCommandKind.ReplaceOne };

            public static EngineCommand Tick() => new() { Kind = EngineCommandKind.Tick };
        }
    }
}
