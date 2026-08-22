// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    // ADR001V03 (see doc/adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md):
    // the Orquestrador's floor guard - the highest-priority signal of all (floor guard > backlog-
    // reactive > manual pin > Monitor). Protects the pool's minimum contractual floor: whenever
    // the buffer's actual, real capacity has dropped below MinCapacity, it triggers an immediate,
    // undebounced replenishment request. This applies uniformly in any mode, including fixed
    // capacity (MinCapacity == MaxCapacity == Capacity) - a failed item replacement (after
    // Invalidate(), or a factory failure during a heartbeat-triggered replacement) can already
    // shrink CurrentCapacity below its intended floor today, with nothing that currently retries
    // it. That live gap is exactly what this guard closes.
    //
    // "available" in the ADR's own wording means the buffer's actual, real capacity
    // (CurrentCapacity) - NOT an idle/unused item count. A breach is CurrentCapacity strictly less
    // than MinCapacity (not "less than or equal"): in a fixed-capacity buffer, CurrentCapacity sits
    // exactly at MinCapacity at rest, so "less than or equal" would make the condition trivially
    // true forever in the common, healthy case. It would still be harmless in practice (the
    // resulting replenishment quantity is zero at exact equality), but strict "less than" avoids
    // the pointless signaling entirely and is the cleaner choice.
    //
    // Wired into RingBufferManager's engine loop via EvaluateFloorGuard (called after ReplaceOne
    // and, like EvaluateBacklogReactive, as a FactoryBatchCompleted follow-up). Ported/designed in
    // isolation first, the same staged approach AutoScaleMonitor.cs (ADR003V03) and, before it, the
    // now-retired AutoScaleDecision.cs used: validate the pure decision logic on its own, wire it
    // in once the surrounding signal-priority model (this same ADR) exists.
    internal static class FloorGuardDecision
    {
        /// <summary>
        /// Decides whether the buffer's actual capacity has breached the minimum contractual floor
        /// and, if so, how many items must be created to restore it.
        /// </summary>
        /// <param name="currentCapacity">
        /// The buffer's actual, real capacity (<c>CurrentCapacity</c>) - not an idle/unused item
        /// count. See the class remarks: this guard is meaningless against an idle-derived proxy.
        /// </param>
        /// <param name="minCapacity">The buffer's minimum contractual capacity.</param>
        /// <returns>
        /// The number of items to create to restore <paramref name="minCapacity"/>, or
        /// <see langword="null"/> when there is no breach (<paramref name="currentCapacity"/> is
        /// already at or above <paramref name="minCapacity"/>).
        /// </returns>
        public static int? EvaluateBreach(int currentCapacity, int minCapacity)
        {
            if (currentCapacity < minCapacity)
            {
                return minCapacity - currentCapacity;
            }
            return null;
        }

        /// <summary>
        /// Decides whether the grace window since a breach was first detected has elapsed - the
        /// point at which the public "below minimum" signal must become true because replenishment
        /// has not (yet) restored the floor. A transient dip that resolves within the window must
        /// never surface this as true.
        /// </summary>
        /// <param name="breachDetectedAt">The instant <see cref="EvaluateBreach"/> first reported a breach.</param>
        /// <param name="now">The current instant - passed explicitly rather than read internally, so this stays a pure function.</param>
        /// <param name="factoryTimeout">
        /// The grace window's duration - the buffer's existing <c>FactoryTimeout</c> parameter,
        /// reused as-is per the ADR (no new configuration surface for this distinction).
        /// </param>
        /// <returns>
        /// <see langword="true"/> once the elapsed time since <paramref name="breachDetectedAt"/>
        /// reaches or exceeds <paramref name="factoryTimeout"/>; <see langword="false"/> while
        /// still within the grace window (including when <paramref name="now"/> is not after
        /// <paramref name="breachDetectedAt"/> at all - never treated as "elapsed").
        /// </returns>
        public static bool HasGraceWindowElapsed(DateTime breachDetectedAt, DateTime now, TimeSpan factoryTimeout)
        {
            var elapsed = now - breachDetectedAt;
            if (elapsed < TimeSpan.Zero)
            {
                return false;
            }
            return elapsed >= factoryTimeout;
        }
    }
}
