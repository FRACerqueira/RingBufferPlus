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
    // Meter/ActivitySource are per-RingBufferManager<T> instance (ADR008), but all instances
    // share the same Name, "RingBufferPlus". So every test gives its buffer a unique name and
    // filters on it - without that, a listener would also pick up managers from every other test
    // class running concurrently in this assembly.
    //
    // Capture collections are ConcurrentQueue<T>, not List<T>, on purpose. These listeners are
    // global: they receive callbacks from every "RingBufferPlus" source in the process, including
    // other tests' managers on other threads. A plain List<T>.Add under that load really does drop
    // entries and cause flaky failures - this has been observed, not just theorized.
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

            // A successful row must still carry acquire.timed_out/acquire.cancelled, both false.
            // Otherwise a consumer filtering on acquire.timed_out=false would get zero rows for
            // every successful acquire, because the tag wouldn't exist at all instead of being false.
            Assert.Contains(durations, r =>
                Equals(r.Tags["acquire.success"], true) &&
                r.Tags.ContainsKey("acquire.timed_out") && Equals(r.Tags["acquire.timed_out"], false) &&
                r.Tags.ContainsKey("acquire.cancelled") && Equals(r.Tags["acquire.cancelled"], false));

            // acquire.warmup_failed belongs on every row too, same tag-contract principle as
            // timed_out/cancelled above.
            Assert.Contains(durations, r => r.Tags.ContainsKey("acquire.warmup_failed") && Equals(r.Tags["acquire.warmup_failed"], false));

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(true, acquireActivity.GetTagItem("success"));
            Assert.Equal(false, acquireActivity.GetTagItem("timed_out"));

            // The Activity must carry "cancelled" on every span, not just the caller-cancellation
            // one - same rule already checked above for the metric.
            Assert.Equal(false, acquireActivity.GetTagItem("cancelled"));
            Assert.Equal(false, acquireActivity.GetTagItem("warmup_failed"));
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

            // Same rule as the success path above: "cancelled" belongs on this row too, not just
            // the caller-cancellation one.
            Assert.Equal(false, acquireActivity.GetTagItem("cancelled"));
            Assert.Equal(false, acquireActivity.GetTagItem("warmup_failed"));
        }

        [Fact]
        public async Task AcquireAsync_AfterWarmupFailureIsCached_StillRecordsDurationAndActivity()
        {
            // A failed initial warmup is cached (ADR011): only an explicit WarmupAsync() call
            // retries it. Every implicit AcquireAsync call after that must still produce a span and
            // an acquire.duration row, not just rethrow the cached exception silently - otherwise a
            // dashboard would show total silence instead of a fault spike while every call fails.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            var manager = new RingBufferManager<int>(CancellationToken.None)
            {
                Name = bufferName,
                Capacity = 2,
                MinCapacity = 1,
                MaxCapacity = 4,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(30),
                SamplesBase = TimeSpan.FromSeconds(30),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(150),
                Elastic = false,
                Factory = _ => throw new InvalidOperationException("boom")
            };

            // The explicit call gets the one-time LogError inside WarmupCoreAsync - not under test here.
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WarmupAsync());

            // Act: an implicit call through AcquireAsync rethrows the same cached failure...
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AcquireAsync().AsTask());

            await manager.DisposeAsync();
            meterListener.Dispose();
            activityListener.Dispose();

            // ...but must still leave a trace on both signals.
            var durations = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(durations, r => Equals(r.Tags["acquire.success"], false));

            // Without acquire.warmup_failed, this row (success/timed_out/cancelled all false) would
            // look identical to an ordinary shutdown race. A metrics-only consumer (no
            // ActivityListener, a normal setup) couldn't tell "the buffer is permanently broken"
            // from "a harmless shutdown". This tag is the only one true on this specific row.
            Assert.Contains(durations, r => Equals(r.Tags["acquire.success"], false) && r.Tags.ContainsKey("acquire.warmup_failed") && Equals(r.Tags["acquire.warmup_failed"], true));

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(false, acquireActivity.GetTagItem("success"));
            Assert.Equal(true, acquireActivity.GetTagItem("warmup_failed"));
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

            // An already-cancelled caller token is a genuine cancellation, not a fault. It
            // propagates, but the span/metric must still record an outcome even though the
            // exception rethrows.
            await Assert.ThrowsAsync<TaskCanceledException>(() => manager.AcquireAsync(callerCts.Token).AsTask());

            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var durations = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.duration" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(durations, r => Equals(r.Tags["acquire.success"], false));

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(false, acquireActivity.GetTagItem("success"));
            Assert.Equal(true, acquireActivity.GetTagItem("cancelled"));
            Assert.Equal(false, acquireActivity.GetTagItem("warmup_failed"));

            // No acquire.faults increment for a caller cancellation - that counter is reserved for
            // genuine AcquireTimeout expirations.
            var faults = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.faults" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName));
            Assert.Empty(faults);
        }

        [Fact]
        [Trait("Category", "Contract")]
        public async Task AcquireDuration_DistinguishesGenuineTimeoutFromCallerCancellation()
        {
            // acquire.duration must distinguish why an acquire failed: a genuine timeout, an
            // ordinary shutdown, and the caller's own cancellation must not look identical. This
            // matches the same distinction the "RingBufferPlus.Acquire" activity already makes.
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
        public async Task HeartBeat_UnhealthyVerdict_RecordsInvalidationCounter()
        {
            // When HeartBeat returns false, the item is invalidated and replaced - the most common
            // outcome for a HeartBeat-configured pool. This needs its own signal; otherwise an
            // operator watching telemetry can't tell a constantly-cycling pool from a healthy one.
            var bufferName = UniqueBufferName();
            var (meterListener, records) = StartMeterListener();

            var manager = new RingBufferManager<int>(CancellationToken.None)
            {
                Name = bufferName,
                Capacity = 2,
                MinCapacity = 2,
                MaxCapacity = 2,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromMilliseconds(50),
                SamplesBase = TimeSpan.FromSeconds(30),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(150),
                BufferHeartBeat = _ => false,
                Factory = _ => Task.FromResult(1)
            };
            await manager.WarmupAsync();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            List<Measurement> invalidations;
            do
            {
                await Task.Delay(25);
                invalidations = records.Where(r => r.InstrumentName == "ringbufferplus.heartbeat.invalidations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            } while (invalidations.Count < 3 && DateTime.UtcNow < deadline);

            await manager.DisposeAsync();
            meterListener.Dispose();

            Assert.True(invalidations.Count >= 3, $"Expected at least 3 heartbeat.invalidations records from repeated unhealthy verdicts, got {invalidations.Count}.");
        }

        [Fact]
        public async Task WaitingCallers_TriggerBacklogReactiveAutoScale_RecordsScaleOperation_WithBacklogTrigger()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            // Capacity=2, MaxCapacity=4: two callers unable to acquire immediately trigger the
            // backlog-reactive signal (ADR001V03). Two waiters, not one, because backlog-reactive
            // scales proportionally to demand, not in one jump to MaxCapacity - it may take more
            // than one small batch to get there.
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
            // The Monitor (ADR001V03/ADR003V03) is the only signal that can scale up before demand
            // actually exceeds capacity - its trend projection can raise the target while demand is
            // still below capacity. Backlog-reactive can't do this (it only fires once a caller is
            // already waiting), so this test uses a demand ramp that never lets idle hit zero: no
            // caller ever waits, yet capacity still grows.
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
            // Floor guard (ADR001V03): MinCapacity == Capacity here, so a single failed replacement
            // immediately breaches the floor. Only the guard's own background retry - not any
            // caller action - restores it once the factory recovers.
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
            // A failed scale operation must not be recorded identically to a successful one.
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
            // A scale operation cancelled by an ordinary DisposeAsync() must not be recorded like a
            // genuine factory failure - same distinction already made for logs, checked here for
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
            // scale.duration must carry the trigger tag too (not just scale.operations), so scale
            // latency can be sliced by which signal (manual/floor/backlog/auto) caused it.
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
            // scale.operations/scale.duration and the "RingBufferPlus.Scale" activity must all
            // carry the target capacity the operation was aiming for - useful for any trigger, not
            // just the Monitor.
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
        public async Task ScaleActivities_CarrySuccessTag_MatchingTheScaleOperationsMetric()
        {
            // scale.operations/scale.duration carry a "success" tag; the "RingBufferPlus.Scale"
            // activity must carry it too - same metric/trace mismatch already fixed for
            // acquire.duration/"RingBufferPlus.Acquire".
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
            await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleUpActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "up"));
            var scaleDownActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName) && Equals(a.GetTagItem("direction"), "down"));
            Assert.Equal(true, scaleUpActivity.GetTagItem("success"));
            Assert.Equal(true, scaleDownActivity.GetTagItem("success"));
        }

        [Fact]
        public async Task ConsecutiveScaleOperations_ProduceIndependentRootActivities_NotChainedToEachOther()
        {
            // This test guards against a plausible-sounding leak that doesn't actually happen:
            // Activity.Current staying set across consecutive Scale operations.
            //
            // Why it seems plausible: StartActivity sets Activity.Current as a side effect, called
            // synchronously from ProcessCommandAsync inside RunEngineAsync's loop. The matching
            // Dispose() runs inside a separate Task.Run body with its own copy of ExecutionContext,
            // so it can't undo that mutation on the engine loop's own context. A standalone repro (a
            // plain sync method in a plain loop) confirmed this part is true, and does leak there.
            //
            // Why it doesn't happen here: every async method call, including `await
            // ProcessCommandAsync(cmd)`, saves and restores the caller's ExecutionContext around
            // itself - even across a purely synchronous call with no actual await inside. So
            // RunEngineAsync's own Activity.Current is restored the instant ProcessCommandAsync
            // returns, regardless of what DispatchScaleUp/DispatchScaleDown mutated it to inside.
            // The standalone repro leaked only because it had no async method boundary between the
            // two StartActivity calls - real code always does.
            var bufferName = UniqueBufferName();
            var (activityListener, activities) = StartActivityListener();

            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>(bufferName, null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 10, 2)
                .LockWhenScaling()
                .BuildWarmupAsync();

            await service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(5));
            await service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(5));
            await service.DisposeAsync();

            activityListener.Dispose();

            var scaleActivities = activities.Where(a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName)).ToList();
            Assert.Equal(2, scaleActivities.Count);
            Assert.All(scaleActivities, a =>
            {
                Assert.Null(a.ParentId);
                Assert.Null(a.Parent);
                Assert.Equal(default, a.ParentSpanId);
            });
        }

        [Fact]
        public async Task AcquireAsync_RacingDisposeAsync_RecordsOkStatus_NotError()
        {
            // An acquire cancelled by an ordinary DisposeAsync() while waiting - not a genuine
            // timeout - must not read as an error span. Same shutdown-vs-failure distinction
            // already made for Scale.
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

            // Assert.All, not Assert.Single/NotEmpty: under heavy parallel test load, this
            // process-wide listener can occasionally miss an activity's stop notification (see the
            // class-level remarks on global listener contamination). When it IS captured, it must
            // never show Error for a shutdown-cancelled (not genuinely timed-out) acquire.
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
            // reason for a per-instance, not static, telemetry source).
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
            // A genuine factory failure can happen earlier in a batch (tolerated via
            // maxConsecutiveFactoryFailures, so the batch continues with partial progress). If an
            // ordinary DisposeAsync() then races the batch's remaining items, the result must still
            // report the genuine failure, not just "cancelled by shutdown" - otherwise the real
            // failure would be masked entirely.
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
            // Triggers the scale-up 2 -> 6 (quantity 4): attempt 1 succeeds, attempt 2 fails
            // genuinely (tolerated), attempts 3/4 are mid-delay when we race them below. SwitchToAsync
            // is used here (not backlog-reactive) only because it gives an exact, deterministic
            // batch size - the telemetry behavior under test doesn't depend on what dispatched it.
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
            // CreateItemsAsync has two exit paths for a failed batch: a tuple return, or - when the
            // batch makes zero progress - throwing lastFailure directly. Both must preserve the
            // genuine-failure vs. cancelled-by-shutdown distinction.
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
            // Triggers the scale-up 2 -> 6: attempt 1 fails genuinely (zero progress, tolerated),
            // attempts 2+ are mid-delay when we race them below. The batch never creates anything,
            // so CreateItemsAsync throws instead of returning.
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
            // RemoveItemsAsync never observes a token and never throws. A scale-down that doesn't
            // fully reach its target just means "not enough idle items were available" - by design,
            // never a genuine failure or a cancellation (see usage-observability.md).
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
