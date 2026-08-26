// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Benchmarks
{
    // NOT a BenchmarkDotNet wall-clock benchmark - it has no [Benchmark] methods. This is a
    // decision-quality simulation: it checks which algorithm scales better, not how fast either
    // one runs. It exists to answer a question ADR003/ADR006 left open: a swap of the old
    // median-sample algorithm should be justified with data, not just because breaking changes
    // are allowed.
    //
    // It compares two capacity-sizing algorithms against the same synthetic demand traces. Both
    // arms share the same instant reactive-escalation rule, so the comparison isolates the slow,
    // corrective decision layer - not the fast reactive path, which both designs already handle
    // the same way.
    //
    //   - MedianDecision: the algorithm v5.x shipped. ADR003V03 replaced it in v6.0.0. This is a
    //     historical copy kept only for comparison - the original internal class and its tests
    //     were deleted once the replacement was wired in, so this copy is now the only place
    //     this algorithm's logic still exists.
    //   - PercentileRegressionDecision: the algorithm ADR003V03 formalized - a sliding-window
    //     percentile plus a safety buffer as a "fair level", adjusted by a linear-regression
    //     trend projected `Horizon` ticks ahead, and clamped to [min, max]. Two refinements were
    //     added after this simulation exposed problems in the raw formula: a deadband, so small
    //     changes below the buffer's own noise tolerance don't move the target (without it, the
    //     formula thrashed under flat-but-noisy demand); and an optional window reset while a
    //     reactive escalation is in flight (without it, a burst already handled by the reactive
    //     path lingered in the window for nearly a full WindowSize, delaying the slow layer's
    //     descent).
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

                Report(name, "Median (v5.x, retired)", demand, medCap, medConverge, medOsc);
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
                // "Active" spans the whole plateau where demand keeps pace with capacity, not
                // just the single tick a reactive jump fires. This mirrors the real engine: a
                // scale operation stays "in flight" for as long as the buffer serves elevated
                // demand, not just the instant it started.
                var active = resetOnReactive && demand[t] >= capacityPrev;

                if (resetOnReactive && wasActive && !active)
                {
                    // Transitioning out of an active episode: purge whatever the window held,
                    // stale or not - exactly like the real engine's reset-on-scale behavior.
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

    // Ported verbatim from the original internal AutoScaleDecision class (since deleted - see
    // the class remarks above on AutoScaleAlgorithmComparison for why this copy exists).
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

    // The Monitor algorithm formalized in ADR003V03: a sliding-window percentile plus a safety
    // buffer as the "fair level", adjusted by a linear-regression trend projected `horizon`
    // ticks ahead, and clamped to [min, max].
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
