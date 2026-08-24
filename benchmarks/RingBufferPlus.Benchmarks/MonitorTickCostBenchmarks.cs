// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using RingBufferPlus.Core;

namespace RingBufferPlus.Benchmarks
{
    // Round 1 (v6 pre-release audit, auditoria-desempenho gap): measures the actual per-tick cost
    // of the Monitor's sample-window management + decision algorithm, at the production default
    // window size (SamplesCount=100) - the numbers `auditoria-complexidade`'s H1/H2 hypotheses
    // (List<int>.RemoveAt(0) shift; Percentile's OrderBy().ToArray() allocation) were left without.
    //
    // This mirrors RingBufferManager.ProcessTick's own sample-window management verbatim (Add then
    // RemoveAt(0) once full) rather than calling AutoScaleMonitor.EvaluateTarget in isolation, so
    // the measured cost is the same shape the engine actually pays once the window is full (the
    // steady-state case - a growing window before that point is cheaper, not the concern H1/H2
    // raised).
    [MemoryDiagnoser]
    public class MonitorTickCostBenchmarks
    {
        private const int SamplesCount = 100; // RingBufferDefault.SampleUnit
        private const double PercentileP = 0.95; // RingBufferDefault.MonitorPercentileP
        private const double SafetyBuffer = 0.10; // RingBufferDefault.MonitorSafetyBuffer
        private const double Horizon = 5; // RingBufferDefault.MonitorHorizon
        private const int Min = 2;
        private const int Max = 64;

        private List<int> _samples = null!;
        private Random _rnd = null!;

        [IterationSetup]
        public void Setup()
        {
            _rnd = new Random(42);
            _samples = new List<int>(SamplesCount + 1);
            for (var i = 0; i < SamplesCount; i++)
            {
                _samples.Add(_rnd.Next(1, 50));
            }
        }

        [Benchmark]
        public int SimulateOneTick_WindowFull()
        {
            _samples.Add(_rnd.Next(1, 50));
            if (_samples.Count > SamplesCount)
            {
                _samples.RemoveAt(0);
            }
            return AutoScaleMonitor.EvaluateTarget(_samples, PercentileP, SafetyBuffer, Horizon, Min, Max);
        }
    }
}
