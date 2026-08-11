// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using RingBufferPlus;

namespace RingBufferPlus.Benchmarks
{
    // ADR008 claims the built-in Meter/ActivitySource instrumentation is near-zero-cost when
    // nothing is listening, and that attaching a listener costs something but not much - this
    // benchmark is the evidence for both halves of that claim (action-plan.md Phase 7, item 7.3),
    // instead of leaving it as an assumption.
    [MemoryDiagnoser]
    public class ObservabilityOverheadBenchmarks
    {
        private IRingBufferService<int> _service = null!;
        private MeterListener? _meterListener;
        private ActivityListener? _activityListener;

        [GlobalSetup(Target = nameof(AcquireAndRelease_NoListener))]
        public void SetupNoListener()
        {
            _service = RingBuffer<int>.New("BenchmarkObservabilityBaseline")
                .Factory(_ => Task.FromResult(1))
                .FixedCapacity(16)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();
        }

        [Benchmark(Baseline = true)]
        public async Task AcquireAndRelease_NoListener()
        {
            await using var value = await _service.AcquireAsync();
        }

        [GlobalSetup(Target = nameof(AcquireAndRelease_WithListener))]
        public void SetupWithListener()
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "RingBufferPlus")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _meterListener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
            _meterListener.Start();

            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "RingBufferPlus",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(_activityListener);

            _service = RingBuffer<int>.New("BenchmarkObservabilityObserved")
                .Factory(_ => Task.FromResult(1))
                .FixedCapacity(16)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();
        }

        [Benchmark]
        public async Task AcquireAndRelease_WithListener()
        {
            await using var value = await _service.AcquireAsync();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _meterListener?.Dispose();
            _activityListener?.Dispose();
        }
    }
}
