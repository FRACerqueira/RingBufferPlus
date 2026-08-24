// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    // ADR008: Meter/ActivitySource are per-RingBufferManager<T> instance, all sharing the
    // constant Name "RingBufferPlus". Every buffer under test gets a unique Name (buffer.name
    // tag), and every assertion filters on it - this suite runs alongside every other test class
    // in the assembly, whose own RingBufferManager<int> instances share the same Meter/ActivitySource
    // Name and would otherwise contaminate a listener that isn't filtering.
    //
    // Capture collections below are ConcurrentQueue<T>, not List<T>, deliberately: these global
    // listeners receive callbacks for every "RingBufferPlus"-named source in the whole process,
    // including from other test classes' managers running concurrently on other threads. A plain
    // List<T>.Add under that concurrent-writer load is a real, observed source of flaky dropped
    // entries - not a hypothetical one.
    public class RingBufferObservabilityTests
    {
        private static string UniqueBufferName([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
            $"{caller}-{Guid.NewGuid():N}";

        private static RingBufferManager<int> CreateManager(
            string bufferName,
            CancellationToken token,
            int capacity = 2,
            int minCapacity = 1,
            int maxCapacity = 4,
            bool elastic = false,
            TimeSpan? acquireTimeout = null)
        {
            return new RingBufferManager<int>(token)
            {
                Name = bufferName,
                Capacity = capacity,
                MinCapacity = minCapacity,
                MaxCapacity = maxCapacity,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(30),
                SamplesBase = TimeSpan.FromSeconds(30),
                SamplesCount = 5,
                AcquireTimeout = acquireTimeout ?? TimeSpan.FromMilliseconds(150),
                Elastic = elastic,
                Factory = (_) => Task.FromResult(1)
            };
        }

        private sealed record Measurement(string InstrumentName, double Value, IReadOnlyDictionary<string, object?> Tags);

        private static (MeterListener Listener, ConcurrentQueue<Measurement> Records) StartMeterListener()
        {
            var records = new ConcurrentQueue<Measurement>();
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "RingBufferPlus")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            void Capture<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _)
                where T : struct =>
                records.Enqueue(new Measurement(instrument.Name, Convert.ToDouble(measurement), tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));

            listener.SetMeasurementEventCallback<long>(Capture);
            listener.SetMeasurementEventCallback<double>(Capture);
            listener.SetMeasurementEventCallback<int>(Capture);
            listener.Start();
            return (listener, records);
        }

        private static (ActivityListener Listener, ConcurrentQueue<Activity> Activities) StartActivityListener()
        {
            var activities = new ConcurrentQueue<Activity>();
            var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "RingBufferPlus",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activities.Enqueue
            };
            ActivitySource.AddActivityListener(listener);
            return (listener, activities);
        }

        [Fact]
        public async Task AcquireAsync_Success_RecordsDurationAndActivity()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = CreateManager(bufferName, cts.Token);
            await manager.WarmupAsync();

            var value = await manager.AcquireAsync();
            Assert.True(value.Successful);
            await value.DisposeAsync();
            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var durations = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(durations, r => Equals(r.Tags["acquire.success"], true));

            // Round 4 (Observabilidade, v6 pre-release audit): the success row must carry
            // acquire.timed_out/acquire.cancelled too (both false), same tag-contract principle
            // DispatchScaleDown's scale.* metrics already follow - otherwise a consumer filtering
            // acquire.timed_out="false" gets zero rows for every successful acquire, since the key
            // simply wouldn't exist on this row, instead of existing as false.
            Assert.Contains(durations, r =>
                Equals(r.Tags["acquire.success"], true) &&
                r.Tags.ContainsKey("acquire.timed_out") && Equals(r.Tags["acquire.timed_out"], false) &&
                r.Tags.ContainsKey("acquire.cancelled") && Equals(r.Tags["acquire.cancelled"], false));

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(true, acquireActivity.GetTagItem("success"));
            Assert.Equal(false, acquireActivity.GetTagItem("timed_out"));

            // Round 5 (Observabilidade, v6 pre-release audit): the Activity had the same tag-contract
            // gap the metric above already had before Round 4 - "cancelled" was only ever set on the
            // caller-cancellation catch, so it was absent (not false) on every other span.
            Assert.Equal(false, acquireActivity.GetTagItem("cancelled"));
        }

        [Fact]
        public async Task AcquireAsync_Timeout_RecordsFaultCounter_AndUnsuccessfulActivity()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = CreateManager(bufferName, cts.Token);
            await manager.WarmupAsync();

            // Drain the pool (Capacity=2) so the next AcquireAsync has nothing available and times out.
            var held1 = await manager.AcquireAsync();
            var held2 = await manager.AcquireAsync();

            var faulted = await manager.AcquireAsync();
            Assert.False(faulted.Successful);

            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var faults = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.faults" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.NotEmpty(faults);

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("success"), false));
            Assert.Equal(true, acquireActivity.GetTagItem("timed_out"));

            // Round 5 (Observabilidade, v6 pre-release audit): same tag-contract gap as the success
            // path - "cancelled" belongs on this row too, not just the caller-cancellation one.
            Assert.Equal(false, acquireActivity.GetTagItem("cancelled"));
        }

        [Fact]
        public async Task AcquireAsync_CallerCancellation_StillRecordsAnOutcome_BeforeRethrowing()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = CreateManager(bufferName, cts.Token);
            await manager.WarmupAsync();

            using var callerCts = new CancellationTokenSource();
            callerCts.Cancel();

            // A caller-supplied, already-cancelled token is a genuine cancellation, not a fault -
            // it propagates (see RingBufferManagerTests.AcquireAsync_ShouldThrow_WhenAlreadyCancelled)
            // but the span/metric must not be left outcome-less just because the exception rethrows.
            await Assert.ThrowsAsync<TaskCanceledException>(() => manager.AcquireAsync(callerCts.Token).AsTask());

            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var durations = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(durations, r => Equals(r.Tags["acquire.success"], false));

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(false, acquireActivity.GetTagItem("success"));
            Assert.Equal(true, acquireActivity.GetTagItem("cancelled"));

            // No acquire.faults increment for a caller cancellation - that counter is reserved for
            // genuine AcquireTimeout expirations.
            var faults = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.faults" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName));
            Assert.Empty(faults);
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AcquireDuration_DistinguishesGenuineTimeoutFromCallerCancellation()
        {
            // Round 5, Observabilidade (finding O6): ringbufferplus.acquire.duration carried no tag
            // distinguishing why an unsuccessful acquire failed - every failed row looked identical
            // whether it was a genuine AcquireTimeout, an ordinary shutdown, or the caller's own
            // token firing, unlike the "RingBufferPlus.Acquire" activity, which already carried
            // timed_out/cancelled. Fixed by adding the same two tags to the metric.
            var timeoutBufferName = UniqueBufferName();
            using var timeoutCts = new CancellationTokenSource();
            var (timeoutMeterListener, timeoutRecords) = StartMeterListener();

            var manager = CreateManager(timeoutBufferName, timeoutCts.Token);
            await manager.WarmupAsync();
            var held1 = await manager.AcquireAsync();
            var held2 = await manager.AcquireAsync();
            var faulted = await manager.AcquireAsync();
            Assert.False(faulted.Successful);
            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await manager.DisposeAsync();
            timeoutMeterListener.Dispose();

            var timeoutDurations = timeoutRecords.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), timeoutBufferName) && Equals(r.Tags.GetValueOrDefault("acquire.success"), false)).ToList();
            Assert.Contains(timeoutDurations, r => Equals(r.Tags.GetValueOrDefault("acquire.timed_out"), true) && Equals(r.Tags.GetValueOrDefault("acquire.cancelled"), false));

            var cancelBufferName = UniqueBufferName();
            using var cancelCts = new CancellationTokenSource();
            var (cancelMeterListener, cancelRecords) = StartMeterListener();

            var manager2 = CreateManager(cancelBufferName, cancelCts.Token);
            await manager2.WarmupAsync();
            using var callerCts = new CancellationTokenSource();
            callerCts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => manager2.AcquireAsync(callerCts.Token).AsTask());
            await manager2.DisposeAsync();
            cancelMeterListener.Dispose();

            var cancelDurations = cancelRecords.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), cancelBufferName) && Equals(r.Tags.GetValueOrDefault("acquire.success"), false)).ToList();
            Assert.Contains(cancelDurations, r => Equals(r.Tags.GetValueOrDefault("acquire.timed_out"), false) && Equals(r.Tags.GetValueOrDefault("acquire.cancelled"), true));
        }

        [Fact]
        public async Task WaitingCallers_TriggerBacklogReactiveAutoScale_RecordsScaleOperation_WithBacklogTrigger()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            // Capacity=2, MaxCapacity=4: two callers unable to acquire immediately trigger the
            // backlog-reactive signal (ADR001V03), which replaced the old fault-count trigger.
            // Two concurrent waiters (not one) are used because backlog-reactive reacts
            // proportionally to the net gap, not necessarily in one single jump to MaxCapacity
            // like the old trigger did - EvaluateBacklogReactive re-checks after every batch
            // completes, so MaxCapacity may be reached via more than one small successive batch.
            var manager = CreateManager(bufferName, cts.Token, capacity: 2, minCapacity: 1, maxCapacity: 4, elastic: true);
            await manager.WarmupAsync();

            var held1 = await manager.AcquireAsync();
            var held2 = await manager.AcquireAsync();
            var waiterTask1 = manager.AcquireAsync().AsTask();
            var waiterTask2 = manager.AcquireAsync().AsTask();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!manager.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            Assert.True(manager.IsMaxCapacity, "Expected the waiting callers to trigger a backlog-reactive scale-up to MaxCapacity.");

            await Task.WhenAll(waiterTask1, waiterTask2);
            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "backlog"));

            Assert.Contains(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up") && Equals(a.GetTagItem("trigger"), "backlog"));
        }

        [Fact]
        public async Task RisingDemandTrend_TriggersPredictiveMonitorScaleUp_BeforeAnyCallerEverWaits_WithAutoTrigger()
        {
            // The Monitor (ADR001V03/ADR003V03) is the only signal that can scale up BEFORE demand
            // actually exceeds capacity: its linear-regression trend, projected a horizon ahead, can
            // push the target above CurrentCapacity from a rising-but-still-below-capacity demand
            // series alone. This is structurally impossible for backlog-reactive (which only exists
            // because a caller could not get an item immediately) or the old median algorithm
            // (scale-down only) - proving it requires a demand ramp that never lets idle hit zero,
            // so no caller ever actually waits.
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = new RingBufferManager<int>(cts.Token)
            {
                Name = bufferName,
                Capacity = 10,
                MinCapacity = 2,
                MaxCapacity = 30,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(30),
                SamplesBase = TimeSpan.FromMilliseconds(1000),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromSeconds(2),
                Elastic = true,
                Factory = _ => Task.FromResult(1)
            };
            await manager.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            var rampDeadline = DateTime.UtcNow.AddSeconds(6);
            for (var i = 0; i < 8 && DateTime.UtcNow < rampDeadline; i++)
            {
                var acquired = await manager.AcquireAsync();
                Assert.True(acquired.Successful, "Expected every ramp acquisition to succeed immediately - the ramp must never make a caller wait, or this would just be backlog-reactive again.");
                held.Add(acquired);
                await Task.Delay(150);
            }

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(6);
            while (manager.CurrentCapacity == 10 && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(50);
            }
            Assert.True(manager.CurrentCapacity > 10, $"Expected the Monitor's rising-trend projection to scale up predictively. Actual: {manager.CurrentCapacity}.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "auto"));

            Assert.Contains(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up") && Equals(a.GetTagItem("trigger"), "auto"));
        }

        [Fact]
        public async Task ReplaceOne_WhenReplacementFactoryFailsThenRecovers_RecordsScaleOperation_WithFloorTrigger()
        {
            // Floor guard (ADR001V03): MinCapacity == Capacity here (like the fixed-capacity
            // scenario in RingBufferContractTests) so a single failed replacement immediately
            // breaches the floor, and the guard's own background retry - not any caller action -
            // is what restores it once the factory recovers.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var shouldFail = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .Build();
            await service.WarmupAsync();

            var acquired = await service.AcquireAsync();
            shouldFail = true;
            acquired.Invalidate();
            await acquired.DisposeAsync(); // triggers ReplaceOne -> CreateSingleReplacementAsync

            var breachDeadline = DateTime.UtcNow.AddSeconds(3);
            while (service.CurrentCapacity == 2 && DateTime.UtcNow < breachDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(1, service.CurrentCapacity);

            shouldFail = false;
            var healDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity < 2 && DateTime.UtcNow < healDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(2, service.CurrentCapacity);

            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "floor") && Equals(r.Tags["success"], true));

            Assert.Contains(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up") && Equals(a.GetTagItem("trigger"), "floor"));
        }

        [Fact]
        public async Task SwitchToAsync_RecordsScaleOperation_WithManualTrigger()
        {
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(_ => Task.FromResult(0))
                .ElasticCapacity(2, 10, 5, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            Assert.True(moved);

            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "manual") && Equals(r.Tags["success"], true));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal("manual", scaleActivity.GetTagItem("trigger"));
            Assert.Equal(ActivityStatusCode.Ok, scaleActivity.Status);
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_WhenFactoryThrowsDuringScaleUp_RecordsFailureTag_AndErrorActivityStatus()
        {
            // A failed/undone scale operation must not be recorded identically to a successful one
            // (finding R8). Reuses the same "factory throws during scale-up" scenario as the P0#1
            // regression test in RingBufferContractTests.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(_ => throwing ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;
            var switchEx = await Record.ExceptionAsync(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1)));
            Assert.IsType<InvalidOperationException>(switchEx);

            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "manual") && Equals(r.Tags["success"], false));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(ActivityStatusCode.Error, scaleActivity.Status);
        }

        [Fact]
        public async Task Warmup_DoesNotRecordAScaleOperation()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();

            var manager = CreateManager(bufferName, cts.Token);
            await manager.WarmupAsync();
            await manager.DisposeAsync();

            meterListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName));
            Assert.Empty(scaleOps);
        }

        [Fact]
        public async Task CapacityGauge_ReportsCurrentCapacity_WhenPolled()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();

            var manager = CreateManager(bufferName, cts.Token, capacity: 3, minCapacity: 1, maxCapacity: 5);
            await manager.WarmupAsync();

            meterListener.RecordObservableInstruments();

            var gauge = records.Where(r => r.InstrumentName == "ringbufferplus.capacity.current" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(gauge, r => r.Value == 3);

            await manager.DisposeAsync();
            meterListener.Dispose();
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task SwitchToAsync_RacingDisposeAsync_RecordsCancelledNotFailure()
        {
            // Round 4, Observabilidade (finding O1): a scale operation cancelled by an ordinary
            // DisposeAsync() must not be recorded identically to a genuine factory failure -
            // same distinction R15/F15/R17/R18 already make for logs, now extended to
            // scale.operations/scale.duration and the "RingBufferPlus.Scale" activity's status.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return 1; }, TimeSpan.FromSeconds(10))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            // Give the engine time to dequeue the Switch command and actually start
            // CreateItemsAsync (the factory is mid-delay) before racing it with a normal dispose.
            await Task.Delay(200);
            await service.DisposeAsync();
            await Record.ExceptionAsync(() => switchTask);

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["success"], false) && Equals(r.Tags["cancelled"], true));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(true, scaleActivity.GetTagItem("cancelled"));
            Assert.Equal(ActivityStatusCode.Ok, scaleActivity.Status);
        }

        [Fact]
        public async Task SwitchToAsync_RecordsScaleDurationTaggedByTrigger()
        {
            // Round 1 (Observabilidade, v6 pre-release audit): scale.duration used to omit the
            // trigger tag entirely (only scale.operations carried it), making it impossible to
            // slice scale latency by which signal (manual/floor/backlog/auto) caused it.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 5, 2)
                .LockWhenScaling()
                .BuildWarmupAsync();

            await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            meterListener.Dispose();

            var scaleDuration = records.Where(r => r.InstrumentName == "ringbufferplus.scale.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleDuration, r => Equals(r.Tags.GetValueOrDefault("trigger"), "manual"));
        }

        [Fact]
        public async Task SwitchToAsync_RecordsTargetTagOnScaleOperationsAndActivity()
        {
            // Round 1 (Observabilidade, v6 pre-release audit): neither scale.operations/
            // scale.duration nor the "RingBufferPlus.Scale" activity carried the target capacity
            // the operation was aiming for - useful for any trigger, not just the Monitor.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 5, 2)
                .LockWhenScaling()
                .BuildWarmupAsync();

            await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags.GetValueOrDefault("target"), 5));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(5, scaleActivity.GetTagItem("target"));
        }

        [Fact]
        public async Task AcquireAsync_RacingDisposeAsync_RecordsOkStatus_NotError()
        {
            // Round 4, Observabilidade (finding O2): AcquireCoreAsync never set an ActivityStatusCode
            // on any outcome before this fix. An acquire cancelled by an ordinary DisposeAsync()
            // while waiting - not a genuine AcquireTimeout - must not read as an error span, mirroring
            // the same shutdown-vs-failure distinction now made for Scale (O1).
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = CreateManager(bufferName, cts.Token, acquireTimeout: TimeSpan.FromSeconds(10));
            await manager.WarmupAsync();

            var held1 = await manager.AcquireAsync();
            var held2 = await manager.AcquireAsync();

            var acquireTask = manager.AcquireAsync().AsTask();
            await Task.Delay(500);
            Assert.False(acquireTask.IsCompleted, "Expected the third acquire to still be blocked (pool exhausted) before racing it with DisposeAsync().");
            await manager.DisposeAsync();

            var result = await acquireTask;
            Assert.False(result.Successful);

            await held1.DisposeAsync();
            await held2.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var faults = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.faults" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName));
            Assert.Empty(faults);

            // Assert.All, not Assert.Single/NotEmpty: under heavy parallel test-suite load, this
            // process-wide ActivityListener can occasionally miss a specific activity's stop
            // notification (a pre-existing harness fragility, not specific to this test - see the
            // class-level remarks on global listener contamination). When the activity IS captured,
            // it must never show Error for a shutdown-cancelled (not genuinely timed-out) acquire.
            var unsuccessfulAcquireActivities = activities.Where(a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("success"), false)).ToList();
            Assert.All(unsuccessfulAcquireActivities, a => Assert.Equal(ActivityStatusCode.Ok, a.Status));
        }

        [Fact]
        public async Task DisposingOneBuffer_DoesNotSilenceTelemetryForAnotherLiveBuffer()
        {
            var nameA = UniqueBufferName();
            var nameB = UniqueBufferName();
            using var ctsA = new CancellationTokenSource();
            using var ctsB = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();

            var managerA = CreateManager(nameA, ctsA.Token);
            var managerB = CreateManager(nameB, ctsB.Token);
            await managerA.WarmupAsync();
            await managerB.WarmupAsync();

            // Dispose A first - this must not affect B's still-live Meter/ActivitySource (ADR008's
            // whole reason for a per-instance, not static, telemetry source).
            await managerA.DisposeAsync();

            var value = await managerB.AcquireAsync();
            Assert.True(value.Successful);
            await value.DisposeAsync();
            await managerB.DisposeAsync();

            meterListener.Dispose();

            var bDurations = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), nameB));
            Assert.Contains(bDurations, r => Equals(r.Tags["acquire.success"], true));
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ScaleUp_WithAGenuineFailureThenRacedByDisposeAsync_StillReportsTheFailure_NotJustCancelled()
        {
            // Round 5, Observabilidade (finding O7): cancelledByShutdown ("!ok && token was
            // cancelled") could be true at the same time a genuine, non-cancellation factory
            // failure had already happened earlier in the very same batch (tolerated via
            // maxConsecutiveFactoryFailures, so the batch kept going and made partial progress).
            // Before the fix, an ordinary DisposeAsync() racing the batch's still-unattempted
            // items reported the whole attempt as "just cancelled by shutdown" - masking the real
            // failure entirely, the exact opposite of what O1 exists to prevent.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var callIndex = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(async ct =>
                {
                    var n = Interlocked.Increment(ref callIndex);
                    if (n <= 2) return 1; // warmup fill (Capacity=2)
                    if (n == 3) return 1; // scale-up attempt 1: succeeds -> created.Count becomes 1
                    if (n == 4) throw new InvalidOperationException("Simulated genuine factory failure.");
                    // scale-up attempts 3+: slow, so DisposeAsync() can race them mid-flight
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return 1;
                }, TimeSpan.FromSeconds(10), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            // Manually triggers the scale-up 2 -> 6 (quantity 4): attempt 1 succeeds, attempt 2
            // fails genuinely (tolerated), attempts 3/4 are mid-Task.Delay when we race them
            // below. What's under test here (CreateItemsAsync's/FactoryBatchCompleted's telemetry
            // correctness under a DisposeAsync race) is identical regardless of what dispatches
            // the batch - SwitchToAsync gives an exact, deterministic quantity (4), unlike
            // backlog-reactive's own proportional sizing (ADR001V03: reacts to the net gap, not a
            // coarse tier jump), which would need several precisely-timed concurrent waiters to
            // reconstruct the same batch size reliably.
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            Assert.True(accepted);

            await Task.Delay(300);
            await service.DisposeAsync();
            await held1.DisposeAsync();
            await held2.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName) && Equals(r.Tags.GetValueOrDefault("direction"), "up")).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags.GetValueOrDefault("success"), false) && Equals(r.Tags.GetValueOrDefault("cancelled"), false));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up"));
            Assert.Equal(false, scaleActivity.GetTagItem("cancelled"));
            Assert.Equal(ActivityStatusCode.Error, scaleActivity.Status);
        }

        [Fact]
        [Trait("Category", "Observability")]
        public async Task ScaleUp_WithAZeroProgressGenuineFailureThenRacedByDisposeAsync_StillReportsTheFailure_NotJustCancelled()
        {
            // Round 6, Resiliência (O7-residual): the O7 fix (above) only threads hadGenuineFailure
            // through CreateItemsAsync's tuple-return exit path. When the whole batch makes zero
            // progress, CreateItemsAsync instead throws its own lastFailure directly - a second,
            // different exit path the O7 fix never covered, reopening the exact same masking.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var callIndex = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(async ct =>
                {
                    var n = Interlocked.Increment(ref callIndex);
                    if (n <= 2) return 1; // warmup fill (Capacity=2)
                    if (n == 3) throw new InvalidOperationException("Simulated genuine factory failure."); // scale-up attempt 1: fails genuinely, zero progress so far
                    // scale-up attempts 2+: slow, so DisposeAsync() can race them mid-flight
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return 1;
                }, TimeSpan.FromSeconds(10), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            // Manually triggers the scale-up 2 -> 6: attempt 1 fails genuinely (zero progress,
            // tolerated), attempts 2+ are mid-Task.Delay when we race them below - the whole batch
            // never creates anything, so CreateItemsAsync throws instead of returning. See the
            // sibling test above for why SwitchToAsync (deterministic quantity), not backlog-
            // reactive, drives this.
            var accepted = await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(1));
            Assert.True(accepted);

            await Task.Delay(300);
            await service.DisposeAsync();
            await held1.DisposeAsync();
            await held2.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName) && Equals(r.Tags.GetValueOrDefault("direction"), "up")).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags.GetValueOrDefault("success"), false) && Equals(r.Tags.GetValueOrDefault("cancelled"), false));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up"));
            Assert.Equal(false, scaleActivity.GetTagItem("cancelled"));
            Assert.Equal(ActivityStatusCode.Error, scaleActivity.Status);
        }

        [Fact]
        [Trait("Category", "Observability")]
        public async Task ScaleDown_WithANormalPartialCompletion_IsNeverReportedAsError()
        {
            // Round 7, shutdown-vs-genuine-failure sweep: RemoveItemsAsync never observes a
            // token and never throws (R6) - a scale-down that doesn't fully reach its target is
            // always just "not enough idle items were available right now," entirely by design,
            // never a genuine failure or a cancellation. Confirmed empirically (before the fix)
            // that this normal outcome was reported as ActivityStatusCode.Error, contradicting
            // usage-observability.md's own documented contract.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 5, 5)
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            // Hold 4 of the 5 items so only 1 is idle - a scale-down to MinCapacity (2, needing
            // to remove 3) can only actually remove 1, a normal partial (not failed) outcome.
            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++) held.Add(await service.AcquireAsync());

            var moved = await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromMinutes(1));
            Assert.False(moved, "Expected a partial (not full) scale-down given only 1 idle item.");

            foreach (var h in held) await h.DisposeAsync();
            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName) && Equals(r.Tags.GetValueOrDefault("direction"), "down")).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags.GetValueOrDefault("success"), false) && Equals(r.Tags.GetValueOrDefault("cancelled"), false));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "down"));
            Assert.Equal(false, scaleActivity.GetTagItem("cancelled"));
            Assert.Equal(ActivityStatusCode.Ok, scaleActivity.Status);
        }
    }
}
