// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using RingBufferPlus;

namespace RingBufferPlus.Benchmarks
{
    // Measures raw AcquireAsync/turnback throughput on a warmed-up, fixed-capacity buffer -
    // the steady-state cost path that has nothing to do with scaling.
    [MemoryDiagnoser]
    public class AcquireThroughputBenchmarks
    {
        private IRingBufferService<int> _service = null!;

        [GlobalSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New("BenchmarkAcquire")
                .Factory(_ => Task.FromResult(1))
                .FixedCapacity(16)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();
        }

        [Benchmark]
        public async Task AcquireAndRelease()
        {
            await using var value = await _service.AcquireAsync();
        }

        [GlobalCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
