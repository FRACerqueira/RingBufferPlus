// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using RingBufferPlus;

namespace RingBufferPlus.Benchmarks
{
    // Measures reaction time to a synthetic load change: exhaust the pool so the next acquire
    // times out, then time how long it takes AutoScaleAcquireFault to react and actually reach
    // MaxCapacity.
    //
    // AutoScaleAcquireFault(0) means the very first timed-out acquire crosses the fault
    // threshold, so the measured duration is dominated by AcquireTimeout plus the actual
    // scale-up cost, not by waiting for repeated faults to accumulate.
    //
    // Each iteration builds a brand-new buffer (IterationSetup/IterationCleanup) because a
    // buffer built with AutoScaleAcquireFault does not expose SwitchToAsync (ADR007) - there is
    // no way to manually reset capacity between iterations, only to start over.
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
    [MemoryDiagnoser]
    public class AutoScaleReactionBenchmarks
    {
        private const int InitialCapacity = 8;
        private IRingBufferService<int> _service = null!;

        [IterationSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New($"BenchmarkReaction-{Guid.NewGuid()}")
                .Factory(_ => Task.FromResult(1))
                .AcquireTimeout(TimeSpan.FromMilliseconds(50))
                .ElasticCapacity(InitialCapacity, 2, 32)
                .AutoScaleAcquireFault(0)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();

            // Exhaust the pool so the next AcquireAsync call below times out instead of succeeding.
            for (var i = 0; i < InitialCapacity; i++)
            {
                _ = _service.AcquireAsync().GetAwaiter().GetResult();
            }
        }

        [Benchmark]
        public async Task ReactToAcquireFault_ScalesFromInitToMax()
        {
            // This acquire times out (pool exhausted); the resulting fault crosses the
            // AutoScaleAcquireFault(0) threshold and triggers a scale-up to MaxCapacity.
            _ = await _service.AcquireAsync();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
        }

        [IterationCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
