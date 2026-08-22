// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Benchmarks
{
    // NOT a BenchmarkDotNet wall-clock benchmark (no [Benchmark] methods) - this is a
    // decision-quality simulation, not a timing measurement. It exists to answer the evidence
    // gap ADR003/ADR006 left open: "a swap of the median-sample autoscaling algorithm for
    // something else should be revisited with data, not because breaking changes are free."
    //
    // It compares two capacity-sizing algorithms against identical synthetic demand traces,
    // sharing an identical instantaneous reactive-escalation rule in both arms so the
    // comparison isolates the slow/corrective decision layer specifically - not the reactive
    // fast path, which both the current design and the v6 proposal already treat the same way.
    //
    //   - MedianDecision: the REAL algorithm shipping today (ported from the internal
    //     RingBufferPlus.Core.AutoScaleDecision - kept as a local copy here because this
    //     project does not have InternalsVisibleTo access; AutoScaleDecisionTests.cs in
    //     RingBufferPlus.Tests is the authoritative test coverage for the original, keep this
    //     copy's logic in sync with it manually if AutoScaleDecision ever changes).
    //   - PercentileRegressionDecision: the v6 design proposal - sliding-window percentile
    //     + safety buffer as a "fair level", adjusted by a linear-regression trend projected
    //     `Horizon` ticks ahead, clamped to [min, max], with a deadband so the target does not
    //     move for changes smaller than the buffer's own noise tolerance (added after this
    //     simulation showed the raw formula thrashes under flat-but-noisy demand), and an
    //     optional window reset while a reactive escalation is in flight (added after this
    //     simulation showed a burst already served by the reactive path otherwise lingers in
    //     the window for close to a full WindowSize, delaying the slow layer's descent).
    //
    // Run with: dotnet run -c Release -- --algo-comparison
    public static class AutoScaleAlgorithmComparison
    {
        private const int Min = 4;
        private const int Init = 8;   // "Capacity" in RingBufferPlus terms
        private const int Max = 32;
        private const int WindowSize = 20;

        // Exact formula from RingBufferBuilder.ValidateBuild (unchanged since v4).
        private const int ScaleDownInitThreshold = Init - Min + 2;
        private const int ScaleDownMaxThreshold = Max - Init + 2;

        private const double PercentileP = 0.95;
        private const double SafetyBuffer = 0.10;
        private const double Horizon = 5;
        private const int Deadband = 3;

        public static void Run()
        {
            var scenarios = new (string Name, int[] Demand, int ChangeTick, string Focus)[]
            {
                ("StepUp", BuildStepUp(), 50, "reactive parity (sanity check, both arms share the same reactive rule)"),
                ("StepDown", BuildStepDown(), 120, "how fast each slow layer shrinks capacity after demand drops"),
                ("SpikeAndRecover", BuildSpike(), 60, "residual lag after a burst shorter than the sampling window"),
                ("NoisyFlat", BuildNoisyFlat(), -1, "oscillation/thrashing under flat-but-noisy demand"),
            };

            Console.WriteLine("Scenario                 | Algorithm                 | AvgCapacity | AvgOverprov | UnmetTicks | ConvergeTicks | Oscillations");
            Console.WriteLine(new string('-', 118));

            foreach (var (name, demand, changeTick, focus) in scenarios)
            {
                // Convergence tolerance is relative to each scenario's own post-change steady
                // demand, not a fixed threshold - SpikeAndRecover's baseline (6) and StepDown's
                // (4) don't share a single meaningful "converged" capacity value.
                var steadyDemand = demand[^1];
                var tolerance = steadyDemand + 3;

                var (medCap, medConverge, medOsc) = SimulateMedian(demand, changeTick, tolerance);
                var (pctCap, pctConverge, pctOsc) = SimulatePercentile(demand, changeTick, tolerance, useDeadband: false, resetOnReactive: false);
                var (pctDbCap, pctDbConverge, pctDbOsc) = SimulatePercentile(demand, changeTick, tolerance, useDeadband: true, resetOnReactive: false);
                var (pctRstCap, pctRstConverge, pctRstOsc) = SimulatePercentile(demand, changeTick, tolerance, useDeadband: true, resetOnReactive: true);

                Report(name, "Median (shipping today)", demand, medCap, medConverge, medOsc);
                Report(name, "Percentile+Regression", demand, pctCap, pctConverge, pctOsc);
                Report(name, "Percentile+Regr.+Deadband", demand, pctDbCap, pctDbConverge, pctDbOsc);
                Report(name, "...+WindowResetOnReactive", demand, pctRstCap, pctRstConverge, pctRstOsc);
                Console.WriteLine($"  -> focus: {focus}");
            }
        }

        private static void Report(string scenario, string algo, int[] demand, int[] cap, int convergeTicks, int oscillations)
        {
            var avgCap = cap.Average();
            var avgOver = demand.Zip(cap, (d, c) => c - d).Average();
            var unmet = demand.Zip(cap, (d, c) => d > c ? 1 : 0).Sum();
            Console.WriteLine($"{scenario,-25} | {algo,-26} | {avgCap,11:F2} | {avgOver,11:F2} | {unmet,10} | {convergeTicks,13} | {oscillations,12}");
        }

        private static int[] BuildStepUp()
        {
            var d = new int[300];
            for (var t = 0; t < 300; t++) d[t] = t < 50 ? 4 : 28;
            return d;
        }

        private static int[] BuildStepDown()
        {
            var d = new int[300];
            for (var t = 0; t < 300; t++) d[t] = t < 120 ? 28 : 4;
            return d;
        }

        private static int[] BuildSpike()
        {
            var d = new int[300];
            for (var t = 0; t < 300; t++) d[t] = (t >= 50 && t < 60) ? 30 : 6;
            return d;
        }

        private static int[] BuildNoisyFlat()
        {
            var rnd = new Random(42);
            var d = new int[300];
            for (var t = 0; t < 300; t++) d[t] = 15 + rnd.Next(-3, 4);
            return d;
        }

        private static (int[] Capacity, int ConvergeTicks, int Oscillations) SimulateMedian(int[] demand, int changeTick, int tolerance)
        {
            var cap = new int[demand.Length];
            var capacityPrev = Init;
            var batch = new List<int>();
            var convergeTicks = -1;

            for (var t = 0; t < demand.Length; t++)
            {
                var capacityReactive = capacityPrev;
                if (demand[t] > capacityPrev)
                {
                    // One tier per reactive trigger, exactly like RingBufferManager's fault-driven ScaleUp.
                    capacityReactive = capacityPrev == Min ? Init : Max;
                }

                var available = Math.Max(capacityReactive - demand[t], 0);
                batch.Add(available);

                var capacityFinal = capacityReactive;
                if (batch.Count == WindowSize)
                {
                    var median = MedianDecision.Median(batch);
                    var isInit = capacityReactive == Init;
                    var isMax = capacityReactive == Max;
                    var result = MedianDecision.EvaluateScaleDown(median, isInit, isMax, Min, Init, ScaleDownInitThreshold, ScaleDownMaxThreshold);
                    capacityFinal = result ?? capacityReactive;
                    batch.Clear();
                }

                cap[t] = capacityFinal;
                capacityPrev = capacityFinal;

                if (changeTick >= 0 && t >= changeTick && convergeTicks == -1 && capacityFinal <= tolerance)
                {
                    convergeTicks = t - changeTick;
                }
            }

            return (cap, convergeTicks, CountOscillations(cap));
        }

        private static (int[] Capacity, int ConvergeTicks, int Oscillations) SimulatePercentile(int[] demand, int changeTick, int tolerance, bool useDeadband, bool resetOnReactive)
        {
            var cap = new int[demand.Length];
            var capacityPrev = Init;
            var window = new List<int>();
            var convergeTicks = -1;
            var wasActive = false;

            for (var t = 0; t < demand.Length; t++)
            {
                // "Active" spans the whole plateau where demand is keeping pace with capacity
                // (not just the single tick a reactive jump fires), mirroring the real engine:
                // a scale operation stays "in flight" for as long as the buffer is servicing an
                // elevated demand level, not just the instant it started.
                var active = resetOnReactive && demand[t] >= capacityPrev;

                if (resetOnReactive && wasActive && !active)
                {
                    // Transitioning out of an active episode: purge whatever the window held,
                    // stale or not, exactly like the real `develop` reset-on-scale behavior.
                    window.Clear();
                }
                wasActive = active;

                var reactiveFloor = demand[t] > capacityPrev ? Math.Min(demand[t], Max) : 0;
                var reactiveTriggered = reactiveFloor > capacityPrev;

                int capacityFinal;
                if (active)
                {
                    // Paused: never consult the (frozen/stale) window while an episode is in
                    // flight - only reactive escalation may still push capacity up further.
                    capacityFinal = Math.Max(reactiveFloor, capacityPrev);
                }
                else
                {
                    window.Add(demand[t]);
                    if (window.Count > WindowSize) window.RemoveAt(0);

                    var monitorTarget = window.Count >= 2
                        ? PercentileRegressionDecision.EvaluateTarget(window, PercentileP, SafetyBuffer, Horizon, Min, Max)
                        : capacityPrev;

                    var candidate = Math.Max(reactiveFloor, monitorTarget);

                    if (reactiveTriggered)
                    {
                        // Safety-critical reactive escalation always applies immediately, never dampened.
                        capacityFinal = candidate;
                    }
                    else if (useDeadband)
                    {
                        capacityFinal = Math.Abs(candidate - capacityPrev) >= Deadband ? candidate : capacityPrev;
                    }
                    else
                    {
                        capacityFinal = candidate;
                    }
                }

                cap[t] = capacityFinal;
                capacityPrev = capacityFinal;

                if (changeTick >= 0 && t >= changeTick && convergeTicks == -1 && capacityFinal <= tolerance)
                {
                    convergeTicks = t - changeTick;
                }
            }

            return (cap, convergeTicks, CountOscillations(cap));
        }

        private static int CountOscillations(int[] cap)
        {
            var count = 0;
            var lastDir = 0;
            for (var t = 1; t < cap.Length; t++)
            {
                var delta = cap[t] - cap[t - 1];
                if (delta == 0) continue;
                var dir = delta > 0 ? 1 : -1;
                if (lastDir != 0 && dir != lastDir) count++;
                lastDir = dir;
            }
            return count;
        }
    }

    // Ported verbatim (logic unchanged) from src/RingBufferPlus/Core/AutoScaleDecision.cs.
    // See the class remarks on AutoScaleAlgorithmComparison for why this copy exists.
    internal static class MedianDecision
    {
        public static double Median(IReadOnlyCollection<int> samples)
        {
            if (samples is null || samples.Count == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(samples));
            }

            var sorted = samples.OrderBy(x => x).ToArray();
            var length = sorted.Length;
            if (length % 2 == 0)
            {
                var pos = length / 2;
                return (sorted[pos - 1] + sorted[pos]) / 2.0;
            }
            var mid = (length + 1) / 2;
            return sorted[mid - 1];
        }

        public static int? EvaluateScaleDown(
            double median,
            bool isInitCapacity,
            bool isMaxCapacity,
            int minCapacity,
            int capacity,
            int? scaleDownInit,
            int? scaleDownMax)
        {
            if (isInitCapacity && scaleDownInit.HasValue && median >= scaleDownInit.Value)
            {
                return minCapacity;
            }
            if (isMaxCapacity && scaleDownMax.HasValue && median > scaleDownMax.Value)
            {
                return capacity;
            }
            return null;
        }
    }

    // The v6 design proposal's Monitor algorithm (see doc/adr conversation history - not yet
    // its own ADR). Sliding-window percentile + safety buffer as the "fair level", adjusted by
    // a linear-regression trend projected `horizon` ticks ahead, clamped to [min, max].
    internal static class PercentileRegressionDecision
    {
        public static double Percentile(IReadOnlyList<int> samples, double p)
        {
            var sorted = samples.OrderBy(x => x).ToArray();
            var rank = p * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            var frac = rank - lo;
            return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
        }

        // Least-squares slope of samples[i] against index i.
        public static double Slope(IReadOnlyList<int> samples)
        {
            var n = samples.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (var i = 0; i < n; i++)
            {
                sumX += i;
                sumY += samples[i];
                sumXY += i * samples[i];
                sumXX += (double)i * i;
            }
            var denom = (n * sumXX) - (sumX * sumX);
            if (denom == 0) return 0;
            return ((n * sumXY) - (sumX * sumY)) / denom;
        }

        public static int EvaluateTarget(IReadOnlyList<int> samples, double percentileP, double safetyBuffer, double horizon, int min, int max)
        {
            var p = Percentile(samples, percentileP);
            var fair = p * (1 + safetyBuffer);
            var slope = Slope(samples);
            var target = fair + (slope * horizon);
            var clamped = Math.Clamp(target, min, max);
            return (int)Math.Ceiling(clamped);
        }
    }
}
