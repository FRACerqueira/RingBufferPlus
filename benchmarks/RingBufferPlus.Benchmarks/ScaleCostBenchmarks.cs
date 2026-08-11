// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using RingBufferPlus;

namespace RingBufferPlus.Benchmarks
{
    // Measures the engine's own cost of moving between capacities via SwitchToAsync, isolated
    // from factory latency (the factory here just returns an int instantly) - action-plan.md
    // Phase 4, "scale up/down cost".
    [MemoryDiagnoser]
    public class ScaleCostBenchmarks
    {
        private IRingBufferManualScaleService<int> _service = null!;

        [GlobalSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New("BenchmarkScale")
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(16, 4, 64)
                .LockWhenScaling()
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();
        }

        [IterationSetup(Target = nameof(ScaleUp_4To64))]
        public void BeforeScaleUp() => _service.SwitchToAsync(ScaleSwitch.MinCapacity).GetAwaiter().GetResult();

        [Benchmark]
        public async Task ScaleUp_4To64() => await _service.SwitchToAsync(ScaleSwitch.MaxCapacity);

        [IterationSetup(Target = nameof(ScaleDown_64To4))]
        public void BeforeScaleDown() => _service.SwitchToAsync(ScaleSwitch.MaxCapacity).GetAwaiter().GetResult();

        [Benchmark]
        public async Task ScaleDown_64To4() => await _service.SwitchToAsync(ScaleSwitch.MinCapacity);

        [GlobalCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
