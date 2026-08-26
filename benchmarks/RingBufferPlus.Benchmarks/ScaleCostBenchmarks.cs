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
    // Measures the engine's own cost of moving between capacities via SwitchToAsync, isolated
    // from factory latency (the factory here just returns an int instantly).
    //
    // RunStrategy.Monitoring pins InvocationCount to 1. The default job would call the
    // benchmarked method many times per [IterationSetup], but IterationSetup only runs once per
    // iteration, not per invocation. Without pinning, every invocation after the first would
    // find the buffer already at its target (an instant no-op via MoveToCapacityAsync's
    // target==current early-return), silently averaging a real scale together with near-zero
    // no-ops.
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 10)]
    [MemoryDiagnoser]
    public class ScaleCostBenchmarks
    {
        private IRingBufferManualScaleService<int> _service = null!;

        [GlobalSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New("BenchmarkScale")
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(4, 64, 16)
                .LockWhenScaling()
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();
        }

        [IterationSetup(Target = nameof(ScaleUp_4To64))]
        public void BeforeScaleUp() => _service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        [Benchmark]
        public async Task ScaleUp_4To64() => await _service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(1));

        [IterationSetup(Target = nameof(ScaleDown_64To4))]
        public void BeforeScaleDown() => _service.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        [Benchmark]
        public async Task ScaleDown_64To4() => await _service.SwitchToAsync(ScaleSwitch.MinCapacity, TimeSpan.FromSeconds(1));

        [GlobalCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
