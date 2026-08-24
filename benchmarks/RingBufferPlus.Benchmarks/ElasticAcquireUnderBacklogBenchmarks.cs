// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using RingBufferPlus;

namespace RingBufferPlus.Benchmarks
{
    // Round 1 (v6 pre-release audit, auditoria-desempenho gap): AcquireThroughputBenchmarks only
    // measures a FixedCapacity buffer (Monitor/backlog-reactive never active at all - the gate in
    // RingBufferManager.cs makes that path structurally untouched by v6's changes). This measures
    // the overhead the v6 concurrency model actually added to the acquire path: waiter accounting
    // (_waitingCount), the gap calculation in EvaluateBacklogReactive, and the Monitor's background
    // sample-tick thread running concurrently - all only present on an elastic buffer.
    //
    // Background "noise" tasks keep the pool under sustained real contention (some callers
    // genuinely waiting, not just idle-served) for the whole benchmark run, so the measured
    // AcquireAndRelease reflects steady-state cost under backlog, not a cold/idle elastic buffer.
    [MemoryDiagnoser]
    public class ElasticAcquireUnderBacklogBenchmarks
    {
        private const int MinCapacity = 4;
        private const int MaxCapacity = 32;
        private const int NoiseCallers = MaxCapacity * 2;

        private IRingBufferService<int> _service = null!;
        private CancellationTokenSource _noiseCts = null!;
        private Task[] _noiseTasks = null!;

        [GlobalSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New("BenchmarkElasticAcquireBacklog")
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(MinCapacity, MaxCapacity, MinCapacity)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();

            _noiseCts = new CancellationTokenSource();
            _noiseTasks = Enumerable.Range(0, NoiseCallers).Select(_ => Task.Run(async () =>
            {
                while (!_noiseCts.IsCancellationRequested)
                {
                    try
                    {
                        await using var value = await _service.AcquireAsync(_noiseCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            })).ToArray();

            // Let the noise ramp up and the Monitor/backlog-reactive settle into a steady
            // contended state (more callers than MaxCapacity, so genuine waiting is sustained)
            // before the benchmarked method starts being measured.
            Thread.Sleep(2000);
        }

        [Benchmark]
        public async Task AcquireAndRelease_UnderSustainedBacklog()
        {
            await using var value = await _service.AcquireAsync();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _noiseCts.Cancel();
            try
            {
                Task.WaitAll(_noiseTasks, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Expected: noise tasks observe cancellation via AcquireAsync's own token.
            }
            _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
