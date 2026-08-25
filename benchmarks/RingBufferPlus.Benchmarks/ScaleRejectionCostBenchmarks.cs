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
    // Measures how fast SwitchToAsync returns `false` (rejected, not queued) when a scale batch
    // is already in flight (ADR001V03: at most one such batch at a time). This isolates the cost
    // of the rejection path itself, not the batch it's rejected against.
    //
    // RunStrategy.Monitoring pins InvocationCount to 1 per iteration, for the same reason as
    // ScaleCostBenchmarks: IterationSetup only runs once per iteration. This benchmark's own
    // precondition - a batch genuinely in flight - would stop holding on a second invocation in
    // the same iteration, since the first rejected call completes almost instantly.
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 10)]
    [MemoryDiagnoser]
    public class ScaleRejectionCostBenchmarks
    {
        private IRingBufferManualScaleService<int> _service = null!;

        [IterationSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New($"BenchmarkScaleRejection-{Guid.NewGuid()}")
                .Factory(async _ => { await Task.Delay(2000); return 1; }, TimeSpan.FromSeconds(5))
                .ElasticCapacity(2, 8, 2)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();

            // Start a slow scale-up in the background - by the time the benchmarked method runs,
            // _scaling is true and stays true for the whole factory delay above, so the
            // benchmarked call is guaranteed to hit the rejection path, not a real dispatch.
            _ = _service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(1));
            Thread.Sleep(50); // let the engine actually dequeue and dispatch the batch
        }

        [Benchmark]
        public async Task<bool> RejectedWhileBatchInFlight() => await _service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(1));

        [IterationCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
