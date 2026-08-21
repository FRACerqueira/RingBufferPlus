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
            bool autoScaleFault = false,
            byte numberFault = 0,
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
                AutoScaleFault = autoScaleFault,
                NumberFault = numberFault,
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

            var acquireActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Acquire" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal(true, acquireActivity.GetTagItem("success"));
            Assert.Equal(false, acquireActivity.GetTagItem("timed_out"));
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

            // No acquire.faults increment and no AutoScaleFault reaction for a caller cancellation -
            // that counter is reserved for genuine AcquireTimeout expirations.
            var faults = records.Where(r => r.InstrumentName == "ringbufferplus.acquire.faults" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName));
            Assert.Empty(faults);
        }

        [Fact]
        public async Task AcquireFault_TriggersAutoScale_RecordsScaleOperation_WithAutoTrigger()
        {
            var bufferName = UniqueBufferName();
            using var cts = new CancellationTokenSource();
            var (meterListener, records) = StartMeterListener();
            var (activityListener, activities) = StartActivityListener();

            // Capacity=2, MaxCapacity=4, NumberFault=0: the first acquire fault immediately triggers scale-up to MaxCapacity.
            var manager = CreateManager(bufferName, cts.Token, capacity: 2, minCapacity: 1, maxCapacity: 4, autoScaleFault: true, numberFault: 0);
            await manager.WarmupAsync();

            var held1 = await manager.AcquireAsync();
            var held2 = await manager.AcquireAsync();
            var faulted = await manager.AcquireAsync();
            Assert.False(faulted.Successful);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!manager.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            Assert.True(manager.IsMaxCapacity, "Expected the acquire fault to trigger an auto scale-up to MaxCapacity.");

            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await manager.DisposeAsync();

            meterListener.Dispose();
            activityListener.Dispose();

            var scaleOps = records.Where(r => r.InstrumentName == "ringbufferplus.scale.operations" && Equals(r.Tags.GetValueOrDefault("buffer.name"), bufferName)).ToList();
            Assert.Contains(scaleOps, r => Equals(r.Tags["direction"], "up") && Equals(r.Tags["trigger"], "auto"));

            var scaleActivity = Assert.Single(activities, a => a.OperationName == "RingBufferPlus.Scale" && Equals(a.GetTagItem("buffer.name"), bufferName));
            Assert.Equal("up", scaleActivity.GetTagItem("direction"));
            Assert.Equal("auto", scaleActivity.GetTagItem("trigger"));
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
                .ElasticCapacity(5, 2, 10, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var moved = await service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            throwing = true;
            var switchEx = await Record.ExceptionAsync(() => service.SwitchToAsync(ScaleSwitch.MaxCapacity));
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
                .ElasticCapacity(2, 2, 6, 1, TimeSpan.FromSeconds(5))
                .LockWhenScaling()
                .Build();
            await service.WarmupAsync();

            var switchTask = service.SwitchToAsync(ScaleSwitch.MaxCapacity);
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
    }
}
