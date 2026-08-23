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
    // Measures reaction time to a synthetic load change under the backlog-reactive signal
    // (ADR001V03, replacing the old AutoScaleAcquireFault fault-count trigger this benchmark
    // originally measured - see its own real numbers, mean 77.46ms, cited as evidence in
    // ADR001V03 for why that trigger was dominated by AcquireTimeout, not the actual cost of
    // scaling).
    //
    // Backlog-reactive reacts the instant a caller starts waiting, before any AcquireTimeout can
    // elapse, proportional to the net gap (waiting callers minus idle items) - not a coarse jump
    // to MaxCapacity on the first fault. To measure "time to reach MaxCapacity" comparably to the
    // old benchmark, this exhausts the pool and then starts exactly (MaxCapacity - InitialCapacity)
    // concurrent waiters, closing the whole gap in a single backlog-reactive batch.
    //
    // Each iteration builds a brand-new buffer (IterationSetup/IterationCleanup): every elastic
    // pool now always has the floor guard/backlog-reactive signal/Monitor active (ADR007V03), so
    // SwitchToAsync could reset capacity between iterations instead, but a fresh buffer avoids any
    // possibility of a Monitor-driven scale-down carrying state across iterations.
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
    [MemoryDiagnoser]
    public class AutoScaleReactionBenchmarks
    {
        private const int InitialCapacity = 8;
        private const int MaxCapacity = 32;
        private IRingBufferService<int> _service = null!;

        [IterationSetup]
        public void Setup()
        {
            _service = RingBuffer<int>.New($"BenchmarkReaction-{Guid.NewGuid()}")
                .Factory(_ => Task.FromResult(1))
                .AcquireTimeout(TimeSpan.FromSeconds(5))
                .ElasticCapacity(2, MaxCapacity, InitialCapacity)
                .BuildWarmupAsync()
                .GetAwaiter().GetResult();

            // Exhaust the pool so every waiter started below is genuinely waiting, not served
            // immediately from an idle item.
            for (var i = 0; i < InitialCapacity; i++)
            {
                _ = _service.AcquireAsync().GetAwaiter().GetResult();
            }
        }

        [Benchmark]
        public async Task ReactToWaitingCallers_ScalesFromInitToMax()
        {
            // Exactly enough concurrent waiters to close the whole gap in one backlog-reactive
            // batch (target = min(CurrentCapacity + gap, MaxCapacity)).
            var waiters = Enumerable.Range(0, MaxCapacity - InitialCapacity)
                .Select(_ => _service.AcquireAsync().AsTask())
                .ToArray();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            await Task.WhenAll(waiters);
        }

        [IterationCleanup]
        public void Cleanup() => _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
