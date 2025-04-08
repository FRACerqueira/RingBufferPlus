// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed class RingBufferManager<T>(CancellationToken lifetimecancellation) : IRingBufferService<T>, IDisposable
    {

        #region fields

        private readonly CancellationTokenSource _managertoken = CancellationTokenSource.CreateLinkedTokenSource(lifetimecancellation);
        private readonly ConcurrentQueue<T> _availableBuffer = [];
        private readonly SemaphoreSlim _semaphoreBuffer = new(1, 1);
        private readonly BlockingCollection<LogMessageBackground> _blockLogger = [];
        private readonly BlockingCollection<ScaleParameters> _blockScale = [];
#if NET9_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif

        private bool _disposed;
        private bool _WarmupDone;
        private bool _WarmupRunning;
        private bool _autoscaleRunning;

        private Task? _taskbufferHeartBeat;
        private Task? _taskbufferLogger;
        private Task? _taskbufferautoscale;
        private Task? _taskbufferNoLockautoscale;

        private int _currentCapacity;

        #endregion

        #region properties

        private int AvailableBuffer => _availableBuffer.Count;

        public bool IsMinCapacity => _currentCapacity == MinCapacity;

        public bool IsMaxCapacity => _currentCapacity == MaxCapacity;

        public bool IsInitCapacity => _currentCapacity == Capacity;

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

        public bool TriggerFault { get; init; }

        public byte NumberFault { get; init; }

        public TimeSpan AcquireTimeout { get; init; }

        public TimeSpan AcquireDelayAttempts { get; init; }

        public ILogger? Logger { get; init; }

        public bool BackgroundLogger { get; init; }

        public bool LockAcquire { get; init; }

        public Action<ILogger?, Exception>? ErrorHandler { get; init; }

        public Action<RingBufferValue<T>>? BufferHeartBeat { get; init; }

        public required Func<CancellationToken, Task<T?>> Factory { get; init; }

        public int CurrentCapacity => _currentCapacity;

        #endregion

        #region IRingBufferService

        public async ValueTask<RingBufferValue<T>> AcquireAsync(CancellationToken cancellation = default)
        {
            if (!_WarmupDone)
            {
                LogWaring("AcquireAsync wait WarmupAsync done...");
                await WarmupAsync(_managertoken.Token);
            }
            if ((cancellation.IsCancellationRequested) || _managertoken.IsCancellationRequested)
            {
#pragma warning disable CS8604 // Possible null reference argument.
                return new RingBufferValue<T>(Name, TimeSpan.Zero, false, default, null);
#pragma warning restore CS8604 // Possible null reference argument.
            }
            var sw = Stopwatch.StartNew();
            if (_autoscaleRunning && LockAcquire)
            {
                LogWaring("AcquireAsync wait autoscale done...");
                while (_autoscaleRunning)
                {
                    try
                    {
                        await Task.Delay(2, cancellation);
                    }
                    catch (OperationCanceledException)
                    {
                        //ignore
                    }
                }
            }
            using var tokentimeoutAcquire = new CancellationTokenSource();
            tokentimeoutAcquire.CancelAfter(AcquireTimeout);
            using var acquiretoken = CancellationTokenSource.CreateLinkedTokenSource(tokentimeoutAcquire.Token, _managertoken.Token, cancellation);
            var qtdfault = 0;
            var TriggerDone = false;
            while (!acquiretoken.IsCancellationRequested)
            {
                var exist = _availableBuffer.TryDequeue(out var result);
                if (exist)
                {
                    return new RingBufferValue<T>(Name, sw.Elapsed, true, result!, DisposeBuffer);
                }
                if (TriggerFault && !TriggerDone && _currentCapacity != MaxCapacity && !_autoscaleRunning)
                {
                    qtdfault++;
                    if (qtdfault > NumberFault && !_autoscaleRunning)
                    {
                        TriggerDone = true;
                        if (IsMinCapacity && !_autoscaleRunning)
                        {
                            try
                            {
                                if (LockAcquire)
                                {
                                    await _semaphoreBuffer.WaitAsync(cancellation);
                                    var qtd = Capacity - _currentCapacity;
                                    _currentCapacity = Capacity;
                                    _semaphoreBuffer.Release();
                                    await ScaleUpProcessAsync(new ScaleParameters(null, ScaleSwitch.MinCapacity, qtd, _managertoken.Token), true);
                                }
                                else
                                {
                                    if (!_autoscaleRunning)
                                    {
                                        lock(_lock)
                                        {
                                            if (!_blockScale.IsAddingCompleted)
                                            {
                                                _autoscaleRunning = true;
                                                _blockScale.Add(new ScaleParameters(ScaleSwitch.InitCapacity, ScaleSwitch.InitCapacity, Capacity - _currentCapacity, _managertoken.Token),_managertoken.Token);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                //ingnore
                            }
                        }
                        else if (IsInitCapacity && !_autoscaleRunning)
                        {
                            try
                            {
                                if (LockAcquire)
                                {
                                    await _semaphoreBuffer.WaitAsync(cancellation);
                                    var qtd = MaxCapacity - _currentCapacity;
                                    _currentCapacity = MaxCapacity;
                                    _semaphoreBuffer.Release();
                                    await ScaleUpProcessAsync(new ScaleParameters(null, ScaleSwitch.InitCapacity, qtd, _managertoken.Token), true);
                                }
                                else
                                {
                                    if (!_autoscaleRunning)
                                    {
                                        lock (_lock)
                                        {
                                            if (!_blockScale.IsAddingCompleted)
                                            {
                                                _autoscaleRunning = true;
                                                _blockScale.Add(new ScaleParameters(ScaleSwitch.MaxCapacity, ScaleSwitch.InitCapacity, MaxCapacity - _currentCapacity, _managertoken.Token),_managertoken.Token);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                //ingnore
                            }
                        }
                        continue;
                    }
                }
                try
                {
                    await Task.Delay(AcquireDelayAttempts, acquiretoken.Token);
                }
                catch (OperationCanceledException)
                {
                    //ignore
                }
            }
            if (acquiretoken.IsCancellationRequested)
            {
                LogWaring("RingBuffer without resource");
            }
            else
            {
                LogError(new TimeoutException("Buffer acquire"));
            }
#pragma warning disable CS8604 // Possible null reference argument.
            return new RingBufferValue<T>(Name, sw.Elapsed, false, default, null);
#pragma warning restore CS8604 // Possible null reference argument.
        }

        public async Task<bool> SwitchToAsync(ScaleSwitch value)
        {
            if (TriggerFault)
            {
                return false;
            }
            if (!_WarmupDone)
            {
                LogWaring("SwitchToAsync wait WarmupAsync done...");
                await WarmupAsync(_managertoken.Token);
            }
            if ((value == ScaleSwitch.MinCapacity && IsMinCapacity) ||
                (value == ScaleSwitch.MaxCapacity && IsMaxCapacity) ||
                (value == ScaleSwitch.InitCapacity && IsInitCapacity))
            {
                return false;
            }
            if (_autoscaleRunning && LockAcquire)
            {
                LogWaring("SwitchToAsync wait autoscalel done...");
                while (_autoscaleRunning)
                {
                    try
                    {
                        await Task.Delay(2, _managertoken.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        //ignore
                    }
                }
            }
            ScaleSwitch origin = ScaleSwitch.InitCapacity;
            if (IsMinCapacity)
            {
                origin = ScaleSwitch.MinCapacity;
            }
            else if (IsMaxCapacity)
            {
                origin = ScaleSwitch.MaxCapacity;
            }


            switch (value)
            {
                case ScaleSwitch.MinCapacity:
                    {
                        var qtd = _currentCapacity - MinCapacity;
                        if (LockAcquire)
                        {
                            _currentCapacity = MinCapacity;
                            await ScaleDownProcessAsync(new ScaleParameters(null, origin, qtd, _managertoken.Token));
                            return _currentCapacity == MinCapacity;
                        }
                        else
                        {
                            if (!_autoscaleRunning)
                            {
                                lock (_lock)
                                {
                                    if (!_blockLogger.IsAddingCompleted)
                                    {
                                        _autoscaleRunning = true;
                                        _blockScale.Add(new ScaleParameters(ScaleSwitch.MinCapacity, origin, qtd, _managertoken.Token));
                                        return true;
                                    }
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                case ScaleSwitch.MaxCapacity:
                    {
                        var qtd = MaxCapacity - _currentCapacity;
                        if (LockAcquire)
                        {
                            _currentCapacity = MaxCapacity;
                            await ScaleUpProcessAsync(new ScaleParameters(null, origin, qtd, _managertoken.Token), true);
                            return _currentCapacity == MaxCapacity;
                        }
                        else 
                        {
                            if (!_autoscaleRunning)
                            {
                                lock (_lock)
                                {
                                    if (!_blockLogger.IsAddingCompleted)
                                    {
                                        _autoscaleRunning = true;
                                        _blockScale.Add(new ScaleParameters(ScaleSwitch.MaxCapacity, origin, qtd, _managertoken.Token),_managertoken.Token);
                                        return true;
                                    }
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                case ScaleSwitch.InitCapacity:
                    {
                        var qtd = Capacity - _currentCapacity;
                        if (LockAcquire)
                        {
                            _currentCapacity = Capacity;
                            if (qtd < 0)
                            {
                            }
                            else if (qtd > 0)
                            {
                                await ScaleUpProcessAsync(new ScaleParameters(null, origin, qtd, _managertoken.Token), true);
                            }
                            return _currentCapacity == Capacity;
                        }
                        else
                        {
                            if (!_autoscaleRunning)
                            {
                                lock (_lock)
                                {
                                    if (!_blockScale.IsAddingCompleted)
                                    {
                                        _autoscaleRunning = true;
                                        _blockScale.Add(new ScaleParameters(ScaleSwitch.InitCapacity, origin, qtd, _managertoken.Token), _managertoken.Token);
                                        return true;
                                    }
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
            }
            return false;
        }

        public async Task WarmupAsync(CancellationToken cancellation = default)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            if (_WarmupDone)
            {
                return;
            }
            await Startup(cancellation);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            if (_disposed) return;
            _disposed = true;
            CleanupResources();
            GC.SuppressFinalize(this);
        }

        #endregion

        private async void DisposeBuffer(RingBufferValue<T> value)
        {
            if (value.Successful)
            {
                if (!value.SkipTurnback)
                {
                    _availableBuffer.Enqueue(value.Current!);
                }
                else
                {
                    if (value.Current is IDisposable itemdispose)
                    {
                        itemdispose.Dispose();
                    }
                    await ScaleUpProcessAsync(new ScaleParameters(null, null, 1, _managertoken.Token), true);
                }
            }
        }

        private void CleanupResources()
        {
            // Cancel the token to all tasks
            _managertoken?.Cancel();

            // wait threads end
            if (_taskbufferHeartBeat is not null)
            {
                _taskbufferHeartBeat?.Wait();
                LogMessage($"Buffer Heart Beat Thread stoped");
            }

            if (_taskbufferNoLockautoscale is not null)
            {
                _taskbufferNoLockautoscale?.Wait();
                LogMessage($"Buffer no lock auto scale Thread stoped");
            }

            if (_taskbufferautoscale is not null)
            {
                _taskbufferautoscale?.Wait();
                LogMessage($"Buffer auto scale Thread stoped");
            }

            if (_taskbufferLogger is not null)
            {
                //set to complete (erro and message null)
                _blockLogger?.Add(new LogMessageBackground(LogLevel.Debug, null, null));
                //wait bufer log empty : mark completed (erro and message null)
                _taskbufferLogger?.Wait();
            }

            _blockLogger?.Dispose();

            _semaphoreBuffer?.Dispose();

            _managertoken?.Dispose();

            //dispose all buffer items
            while (_availableBuffer.TryDequeue(out var itembuffer))
            {
                if (itembuffer is IDisposable itemdispose)
                {
                    itemdispose.Dispose();
                }
            }
            _currentCapacity = _availableBuffer.Count;
        }

        private async Task Startup(CancellationToken cancellation)
        {

            if (_WarmupDone || _WarmupRunning)
            {
                return;
            }
            try
            {
                await _semaphoreBuffer.WaitAsync(cancellation);
                _WarmupRunning = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _semaphoreBuffer.Release();
            }

            _managertoken.Token.Register(() => Dispose());

            CreateTaskbufferLogger();

            _currentCapacity = Capacity;

            LogMessage("Starting warmup process.");

            await ScaleUpProcessAsync(new ScaleParameters(null, ScaleSwitch.InitCapacity, Capacity, cancellation), false);

            if (AvailableBuffer != Capacity)
            {
                var err = new InvalidOperationException("RingBuffer did not reach initial capacity");
                LogError(err);
                throw err;
            }
            _WarmupRunning = false;
            _WarmupDone = true;
            LogMessage($"End warmup process with {AvailableBuffer} buffers.");

            CreateTaskbufferHeartBeat();
            CreateTaskbufferAutoScale();
        }

        private void CreateTaskbufferLogger()
        {
            if (!BackgroundLogger || (Logger is null && ErrorHandler is null))
            {
                return;
            }
            _taskbufferLogger = Task.Run(() =>
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: Buffer logger Thread Created";
                logMessageForDbg(Logger!, Name, msg, null);
                try
                {
                    foreach (var item in _blockLogger.GetConsumingEnumerable())
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
                            if (ErrorHandler == null)
                            {
                                msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {item.Error.Message} ";
                                logMessageForErr(Logger!, Name, msg, item.Error);
                            }
                            else
                            {
                                ErrorHandler?.Invoke(Logger, item.Error);
                            }
                        }
                        if (item.Error is  null && item.Message is null)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //ignore
                }
                finally
                {
                    msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: Buffer logger Thread stoped";
                    logMessageForDbg(Logger!, Name, msg, null);
                }
            }); 
        }

        private void CreateTaskbufferHeartBeat()
        {
            if (BufferHeartBeat is null) return;

            _taskbufferHeartBeat = Task.Run(async () =>
            {
                LogMessage($"Buffer Heart Beat Thread Created");
                //wait Warmup
                if (!_managertoken.IsCancellationRequested && !_WarmupDone)
                {
                    LogMessage($"Heart Beat Thread Wait Warmup done");
                    while (!_managertoken.IsCancellationRequested && !_WarmupDone)
                    {
                        _managertoken.Token.WaitHandle.WaitOne(5);
                    }
                }
                var skippulse = false;
                while (!_managertoken.IsCancellationRequested)
                {
                    if (!skippulse)
                    {
                        try
                        {
                            await Task.Delay(PulseHeartBeat, _managertoken.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }
                    }
                    LogMessage("Started Heart Beat item");
                    skippulse = false;
                    using var taskAcquire = await AcquireAsync(_managertoken.Token);
                    var sw = new Stopwatch();
                    if (taskAcquire.Successful)
                    {
                        using var tokentimeoutPulseHeartBeat = new CancellationTokenSource();
                        tokentimeoutPulseHeartBeat.CancelAfter(PulseHeartBeat);
                        try
                        {
                            var taskaux = Task.Run(() =>
                            {
                                BufferHeartBeat?.Invoke(taskAcquire);
                            }, tokentimeoutPulseHeartBeat.Token);
                            await taskaux;
                        }
                        catch (OperationCanceledException)
                        {
                            var err = new TimeoutException("Timeout Heart Beat");
                            LogError(err);
                            skippulse = true;
                        }
                        catch (Exception ex)
                        {
                            LogError(ex);
                        }
                    }
                    else
                    {
                        LogMessage("Heart Beat item not available");
                    }
                    LogMessage($"Stoped Heart Beat item after {sw.Elapsed}");
                }
            });
        }

        private void CreateTaskbufferAutoScale()
        {
            if (!LockAcquire)
            {
                CreateTaskbufferNoLockAutoScale();
            }

            if (!TriggerFault)
            {
                return;
            }

            _taskbufferautoscale = Task.Run(async () =>
            {
                LogMessage($"Buffer auto scale Thread Created");
                //wait Warmup
                if (!_managertoken.IsCancellationRequested && !_WarmupDone)
                {
                    LogMessage($"Auto scale Thread Wait Warmup done");
                    while (!_managertoken.IsCancellationRequested && !_WarmupDone)
                    {
                        _managertoken.Token.WaitHandle.WaitOne(5);
                    }
                }
                LogMessage($"Auto scale Thread Wait initial {SamplesBase}");
                try
                {
                    await Task.Delay(SamplesBase, _managertoken.Token);
                }
                catch (OperationCanceledException)
                {
                    //ingnore
                }
                var metricBuffer = new List<int>();
                var delay = TimeSpan.FromMilliseconds(SamplesBase.TotalMilliseconds / SamplesCount);
                while (!_managertoken.IsCancellationRequested)
                {
                    if (!_autoscaleRunning)
                    {
                        metricBuffer.Add(AvailableBuffer);
                    }
                    if (!_autoscaleRunning && metricBuffer.Count >= SamplesCount)
                    {
                        var tmp = metricBuffer.OrderBy(x => x).ToArray();
                        double median = 0;
                        if (tmp.Length % 2 == 0)
                        {
                            var pos = tmp.Length / 2;
                            median = (tmp[pos - 1] + tmp[pos]) / 2.0;
                        }
                        else
                        {
                            var pos = (tmp.Length + 1) / 2;
                            median = tmp[pos - 1];
                        }
                        metricBuffer.Clear();
                        var medianint = Convert.ToInt32(Math.Ceiling(median));
                        if (!_autoscaleRunning && IsInitCapacity && medianint >= ScaleDownInit)
                        {
                            await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                            var qtd = _currentCapacity - MinCapacity;
                            _currentCapacity = MinCapacity;
                            _semaphoreBuffer.Release();
                            await ScaleDownProcessAsync(new ScaleParameters(null, ScaleSwitch.InitCapacity, qtd, _managertoken.Token));
                        }
                        else if (!_autoscaleRunning && IsMaxCapacity && medianint > ScaleDownMax)
                        {
                            await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                            var qtd = _currentCapacity - Capacity;
                            _currentCapacity = Capacity;
                            _semaphoreBuffer.Release();
                            await ScaleDownProcessAsync(new ScaleParameters(null, ScaleSwitch.MaxCapacity, qtd, _managertoken.Token));
                        }
                    }
                    try
                    {
                        if (_autoscaleRunning && metricBuffer.Count > 0)
                        {
                            metricBuffer.Clear();
                        }
                        await Task.Delay(delay, _managertoken.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        //ingnore
                    }
                }
            });
        }

        private void CreateTaskbufferNoLockAutoScale()
        {
            _taskbufferNoLockautoscale = Task.Run(async () =>
            {
                LogMessage($"Buffer no lock auto scale Thread Created");
                //wait Warmup
                if (!_managertoken.IsCancellationRequested && !_WarmupDone)
                {
                    LogMessage($"Auto scale Thread Wait Warmup done");
                    while (!_managertoken.IsCancellationRequested && !_WarmupDone)
                    {
                        _managertoken.Token.WaitHandle.WaitOne(5);
                    }
                }
                try
                {
                    foreach (var item in _blockScale.GetConsumingEnumerable(_managertoken.Token))
                    {
                        switch (item.Scale)
                        {
                            case null:

                            case ScaleSwitch.MinCapacity when !IsMinCapacity:
                                await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                                _currentCapacity = MinCapacity;
                                _semaphoreBuffer.Release();
                                await ScaleDownProcessAsync(new ScaleParameters(null, item.Origin, item.Quantity, item.Token));
                                break;
                            case ScaleSwitch.MaxCapacity when !IsMaxCapacity:
                                await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                                _currentCapacity = MaxCapacity;
                                _semaphoreBuffer.Release();
                                await ScaleUpProcessAsync(new ScaleParameters(null, item.Origin, item.Quantity, item.Token), true);
                                break;
                            case ScaleSwitch.InitCapacity when !IsInitCapacity:
                                if (item.Quantity < 0)
                                {
                                    await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                                    _currentCapacity = Capacity;
                                    _semaphoreBuffer.Release();
                                    await ScaleDownProcessAsync(new ScaleParameters(null, item.Origin, item.Quantity, item.Token));
                                }
                                else
                                {
                                    await _semaphoreBuffer.WaitAsync(_managertoken.Token);
                                    _currentCapacity = Capacity;
                                    _semaphoreBuffer.Release();
                                    await ScaleUpProcessAsync(new ScaleParameters(null, item.Origin, item.Quantity, item.Token), true);
                                }
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //ignore
                }
                finally
                {
                    //clear buffer scale
                    while (_blockScale.TryTake(out _)) { };
                }
            });
        }

        private async Task ScaleUpProcessAsync(ScaleParameters item, bool hastimeout)
        {
            if (item.Quantity == 0)
            {
                _autoscaleRunning = false;
                return;
            }

            var localbuffer = new List<T>();
            var qtd = 0;
            try
            {
                await _semaphoreBuffer.WaitAsync(item.Token);
                _autoscaleRunning = true;
                LogMessage($"Starting ScaleUp {item.Quantity}.");
                using var tokenScaleUp = CancellationTokenSource.CreateLinkedTokenSource(item.Token);
                if (hastimeout)
                {
                    tokenScaleUp.CancelAfter(SamplesBase);
                }

                while (qtd < item.Quantity && !tokenScaleUp.IsCancellationRequested)
                {
                    using var tokentimeoutfactory = CancellationTokenSource.CreateLinkedTokenSource(tokenScaleUp.Token);
                    tokentimeoutfactory.CancelAfter(FactoryTimeout);
                    try
                    {
                        while (!tokentimeoutfactory.IsCancellationRequested)
                        {
                            var newitem = await Factory(tokenScaleUp.Token);
                            if (newitem is not null && !tokenScaleUp.IsCancellationRequested)
                            {
                                localbuffer.Add(newitem);
                                qtd++;
                                break;
                            }
                            else
                            {
                                if (newitem is not null && newitem is IDisposable itemdispose)
                                {
                                    itemdispose?.Dispose();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (tokentimeoutfactory.IsCancellationRequested)
                        {
                            var err = new TimeoutException("Timeout factory");
                            LogError(err);
                        }
                        if (tokenScaleUp.IsCancellationRequested)
                        {
                            foreach (var itemtoadd in localbuffer)
                            {
                                if (itemtoadd is IDisposable itemdispose)
                                {
                                    itemdispose?.Dispose();
                                }
                            }
                            var err = new TimeoutException($"Timeout ScaleUp {qtd}/{item.Quantity}.");
                            LogError(err);
                            if (qtd < item.Quantity && qtd != 0)
                            {
                                switch (item.Origin)
                                {
                                    case ScaleSwitch.MinCapacity:
                                        _currentCapacity = MinCapacity;
                                        break;
                                    case ScaleSwitch.MaxCapacity:
                                        _currentCapacity = MaxCapacity;
                                        break;
                                    case ScaleSwitch.InitCapacity:
                                        _currentCapacity = Capacity;
                                        break;
                                }
                            }
                        }
                        localbuffer.Clear();
                        return;
                    }
                }
                foreach (var itemtoadd in localbuffer)
                {
                    _availableBuffer.Enqueue(itemtoadd);
                }
                localbuffer.Clear();
            }
            catch (OperationCanceledException)
            {
                localbuffer.Clear();
                return;
            }
            finally
            {
                _autoscaleRunning = false;
                _semaphoreBuffer.Release();
                LogMessage("End ScaleUp.");
            }
        }

        private async Task ScaleDownProcessAsync(ScaleParameters item)
        {
            if (item.Quantity == 0)
            {
                _autoscaleRunning = false;
                return;
            }
            using var tokenScaleDown = CancellationTokenSource.CreateLinkedTokenSource(item.Token);
            tokenScaleDown.CancelAfter(SamplesBase);
            var qtd = 0;
            var removeBufferItem = new List<T>();
            try
            {
                await _semaphoreBuffer.WaitAsync(item.Token);
                _autoscaleRunning = true;
                LogMessage($"Starting ScaleDown {item.Quantity}.");
                try
                {
                    while (qtd < item.Quantity && !tokenScaleDown.IsCancellationRequested)
                    {
                        if (_availableBuffer.TryDequeue(out var itembuffer))
                        {
                            removeBufferItem.Add(itembuffer);
                            qtd++;
                        }
                        else
                        {
                            await Task.Delay(2, tokenScaleDown.Token);
                        }
                    }
                    foreach (var itemdtoispose in removeBufferItem)
                    {
                        if (itemdtoispose is IDisposable itemdispose)
                        {
                            itemdispose.Dispose();
                        }
                    }
                    removeBufferItem.Clear();
                }
                catch (OperationCanceledException)
                {
                    if (!_managertoken.Token.IsCancellationRequested)
                    {
                        var err = new TimeoutException($"Timeout ScaleDown {qtd}/{item.Quantity}.");
                        LogError(err);
                    }
                    foreach (var itemtoadd in removeBufferItem)
                    {
                        _availableBuffer.Enqueue(itemtoadd);
                    }
                    if (qtd < item.Quantity && qtd != 0)
                    {
                        switch (item.Origin)
                        {
                            case ScaleSwitch.MaxCapacity:
                                _currentCapacity = MaxCapacity;
                                break;
                            case ScaleSwitch.InitCapacity:
                                _currentCapacity = Capacity;
                                break;
                        }
                    }
                    removeBufferItem.Clear();
                    return;
                }

            }
            catch (OperationCanceledException)
            {
                removeBufferItem.Clear();
                return;
            }
            finally
            {
                _autoscaleRunning = false;
                _semaphoreBuffer.Release();
                LogMessage($"End ScaleDown.");
            }
        }

        private void LogMessage(string message)
        {
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            try
            {
                if (Logger is null || string.IsNullOrEmpty(msg)) return;

                if (BackgroundLogger)
                {
                    _blockLogger.Add(new LogMessageBackground(LogLevel.Debug,msg, null));
                }
                else
                {
                    logMessageForDbg(Logger!, Name, msg, null);
                }

            }
            catch (ObjectDisposedException)
            {
                //ingnore
            }
        }

        private void LogWaring(string message)
        {
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            try
            {
                if (Logger is null || string.IsNullOrEmpty(msg)) return;

                if (BackgroundLogger)
                {
                    _blockLogger.Add(new LogMessageBackground(LogLevel.Warning, msg, null));
                }
                else
                {
                    logMessageFoWrn(Logger!, Name, msg, null);
                }
            }
            catch (ObjectDisposedException)
            {
                //ignore
            }
        }

        private void LogError(Exception error)
        {
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {error.Message} ";

            try
            {
                if (Logger is null && ErrorHandler is null) return;

                if (BackgroundLogger)
                {
                    _blockLogger.Add(new LogMessageBackground(LogLevel.Error, null, error));
                }
                else
                {
                    if (ErrorHandler == null)
                    {
                        logMessageForErr(Logger!, Name, msg, error);
                    }
                    else
                    {
                        ErrorHandler?.Invoke(Logger, error);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                //ignore
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({source}) : {message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferManager({source}) : {message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageFoWrn = LoggerMessage.Define<string, string>(LogLevel.Warning, 0, "RingBufferManager({source}) : {message}");

    }
}

