// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    // ADR001V03 (see doc/adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md):
    // the Orchestrator's floor guard. It is the highest-priority of the four scale signals
    // (floor guard > backlog-reactive > manual pin > Monitor).
    //
    // It protects the pool's minimum contractual floor. Whenever the buffer's real capacity drops
    // below MinCapacity, it triggers an immediate, undebounced replenishment request. This applies
    // in every mode, including fixed capacity (MinCapacity == MaxCapacity == Capacity): a failed
    // item replacement - after Invalidate(), or a factory failure during a heartbeat-triggered
    // replacement - can already shrink CurrentCapacity below its floor today, and nothing else
    // retries it. This guard closes that gap.
    //
    // "Available" in the ADR means the buffer's real capacity (CurrentCapacity), not an idle/unused
    // item count. A breach is CurrentCapacity strictly less than MinCapacity, not "less than or
    // equal". A fixed-capacity buffer sits exactly at MinCapacity at rest, so "less than or equal"
    // would always be true there. That would still be harmless (the replenishment quantity would
    // be zero), but strict "less than" avoids the pointless signal and is simply cleaner.
    //
    // Wired into RingBufferManager's engine loop via EvaluateFloorGuard, called after ReplaceOne
    // and after every FactoryBatchCompleted, same as EvaluateBacklogReactive. Designed and tested
    // in isolation first, then wired into the engine - the same staged approach AutoScaleMonitor.cs
    // (ADR003V03) uses.
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
        /// Decides whether the grace window since a breach was first detected has elapsed. Once it
        /// has, the public "below minimum" signal must become true, because replenishment hasn't
        /// restored the floor in time. A transient dip that resolves within the window must never
        /// trigger this.
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

        /// <summary>
        /// Decides whether the "below minimum" report should fire right now. It fires once when the
        /// grace window first elapses, then again on that same cadence as a fallback - so a
        /// persistently broken factory is never reported once and then goes silent for the rest of
        /// the outage. The report repeats on its own clock, independent of whatever retry cadence
        /// surrounds it.
        /// </summary>
        /// <param name="breachDetectedAt">The instant <see cref="EvaluateBreach"/> first reported a breach.</param>
        /// <param name="lastReportedAt">
        /// The instant the report last fired, or <see langword="null"/> if it has never fired for
        /// this breach.
        /// </param>
        /// <param name="now">The current instant - passed explicitly, so this stays a pure function.</param>
        /// <param name="factoryTimeout">The grace window's duration, reused as the re-report cadence too.</param>
        /// <returns>
        /// <see langword="true"/> the first time the grace window elapses, then again every time a
        /// further <paramref name="factoryTimeout"/> has elapsed since <paramref name="lastReportedAt"/>.
        /// </returns>
        public static bool ShouldReportNow(DateTime breachDetectedAt, DateTime? lastReportedAt, DateTime now, TimeSpan factoryTimeout)
        {
            if (!HasGraceWindowElapsed(breachDetectedAt, now, factoryTimeout))
            {
                return false;
            }
            return lastReportedAt is null || HasGraceWindowElapsed(lastReportedAt.Value, now, factoryTimeout);
        }
    }
}
