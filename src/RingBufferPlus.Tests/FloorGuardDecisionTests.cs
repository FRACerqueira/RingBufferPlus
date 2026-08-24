// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Acceptance tests for the v6.0.0 floor guard (ADR001V03), ported and validated in isolation
// before being wired into the engine - see the class remarks on FloorGuardDecision.

using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public class FloorGuardDecisionTests
    {
        [Fact]
        public void EvaluateBreach_CurrentAboveMin_ReturnsNull()
        {
            Assert.Null(FloorGuardDecision.EvaluateBreach(currentCapacity: 5, minCapacity: 2));
        }

        [Fact]
        public void EvaluateBreach_CurrentExactlyAtMin_ReturnsNull()
        {
            // The common, healthy steady state for a fixed-capacity buffer (MinCapacity ==
            // MaxCapacity == Capacity) - must not be a breach, or this would fire constantly.
            Assert.Null(FloorGuardDecision.EvaluateBreach(currentCapacity: 4, minCapacity: 4));
        }

        [Fact]
        public void EvaluateBreach_CurrentBelowMin_ReturnsExactGap()
        {
            Assert.Equal(3, FloorGuardDecision.EvaluateBreach(currentCapacity: 2, minCapacity: 5));
        }

        [Fact]
        public void EvaluateBreach_CurrentOneBelowMin_ReturnsOne()
        {
            Assert.Equal(1, FloorGuardDecision.EvaluateBreach(currentCapacity: 3, minCapacity: 4));
        }

        [Fact]
        public void EvaluateBreach_AtMinimumLegalCapacity_StillDetectsBreach()
        {
            // MinCapacity == 2 is the minimum legal value across this codebase's own validation
            // (RingBufferBuilder.ValidateBuild) - the guard must not have a blind spot there.
            Assert.Equal(1, FloorGuardDecision.EvaluateBreach(currentCapacity: 1, minCapacity: 2));
        }

        [Fact]
        public void EvaluateBreach_CurrentZero_ReturnsFullMinCapacity()
        {
            // A fully-emptied floor (every item lost) is still just a larger instance of the same
            // gap - no special-casing needed.
            Assert.Equal(2, FloorGuardDecision.EvaluateBreach(currentCapacity: 0, minCapacity: 2));
        }

        [Fact]
        public void HasGraceWindowElapsed_WellWithinWindow_ReturnsFalse()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = start + TimeSpan.FromSeconds(1);
            Assert.False(FloorGuardDecision.HasGraceWindowElapsed(start, now, TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void HasGraceWindowElapsed_JustBeforeTheWindow_ReturnsFalse()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = start + TimeSpan.FromSeconds(15) - TimeSpan.FromMilliseconds(1);
            Assert.False(FloorGuardDecision.HasGraceWindowElapsed(start, now, TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void HasGraceWindowElapsed_ExactlyAtTheBoundary_ReturnsTrue()
        {
            // Deliberately ">=", not ">" - a genuinely broken factory must be reported truthfully
            // once a full FactoryTimeout cycle has passed, not one tick later.
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = start + TimeSpan.FromSeconds(15);
            Assert.True(FloorGuardDecision.HasGraceWindowElapsed(start, now, TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void HasGraceWindowElapsed_WellPastTheWindow_ReturnsTrue()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = start + TimeSpan.FromSeconds(30);
            Assert.True(FloorGuardDecision.HasGraceWindowElapsed(start, now, TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public void HasGraceWindowElapsed_ZeroFactoryTimeout_ElapsesImmediately()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(FloorGuardDecision.HasGraceWindowElapsed(start, start, TimeSpan.Zero));
        }

        [Fact]
        public void HasGraceWindowElapsed_NowBeforeBreachDetectedAt_ReturnsFalse()
        {
            // A caller passing a `now` earlier than the breach's own detected-at instant (e.g. a
            // clock artifact, or a stale/reordered call) must never be treated as "elapsed" - the
            // negative elapsed time is clamped to "still within the window", not sign-flipped into
            // an even larger apparent elapsed duration.
            var breachDetectedAt = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
            var now = breachDetectedAt - TimeSpan.FromSeconds(5);
            Assert.False(FloorGuardDecision.HasGraceWindowElapsed(breachDetectedAt, now, TimeSpan.FromSeconds(15)));
        }

        // ---------------------------------------------------------------------
        // ShouldReportNow (Round 10, Observabilidade): the "below minimum" report must fire once
        // when the grace window first elapses, then again on that same cadence as a fallback -
        // never on every single evaluation (that was the bug: unbounded repeats driven by whatever
        // cadence the caller happened to re-evaluate at), and never silent forever after the first
        // report either.
        // ---------------------------------------------------------------------

        [Fact]
        public void ShouldReportNow_BeforeGraceWindowElapses_ReturnsFalse_RegardlessOfLastReportedAt()
        {
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = detectedAt + TimeSpan.FromMilliseconds(100);
            Assert.False(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt: null, now, TimeSpan.FromMilliseconds(150)));
        }

        [Fact]
        public void ShouldReportNow_GraceWindowJustElapsed_NeverReportedBefore_ReturnsTrue()
        {
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = detectedAt + TimeSpan.FromMilliseconds(150);
            Assert.True(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt: null, now, TimeSpan.FromMilliseconds(150)));
        }

        [Fact]
        public void ShouldReportNow_SoonAfterTheFirstReport_ReturnsFalse()
        {
            // This is the exact bug: without a latch, this call - happening while the grace window
            // was already elapsed since detection - would report again immediately. The fix's whole
            // point is that "elapsed since detection" is no longer sufficient once a report has
            // already fired; it must also be a fresh grace-window's worth of time since that report.
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastReportedAt = detectedAt + TimeSpan.FromMilliseconds(150);
            var now = lastReportedAt + TimeSpan.FromMilliseconds(50);
            Assert.False(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt, now, TimeSpan.FromMilliseconds(150)));
        }

        [Fact]
        public void ShouldReportNow_ManyRapidRetriesWithinOneWindow_OnlyTheFirstReports()
        {
            // Simulates the real failure mode: several evaluations happening well inside a single
            // grace-window's worth of time (e.g. a batch of concurrent retry attempts all completing
            // in quick succession). Only the first should report; the rest, still within the same
            // window since that report, must not.
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var factoryTimeout = TimeSpan.FromMilliseconds(150);
            var firstReportAt = detectedAt + factoryTimeout;
            Assert.True(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt: null, firstReportAt, factoryTimeout));

            DateTime? lastReportedAt = firstReportAt;
            foreach (var offsetMs in new[] { 10, 30, 60, 90, 120 })
            {
                var now = firstReportAt + TimeSpan.FromMilliseconds(offsetMs);
                Assert.False(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt, now, factoryTimeout));
            }
        }

        [Fact]
        public void ShouldReportNow_ExactlyOneWindowAfterTheLastReport_ReturnsTrue_ProvingItIsNotSilentForever()
        {
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var factoryTimeout = TimeSpan.FromMilliseconds(150);
            var lastReportedAt = detectedAt + factoryTimeout;
            var now = lastReportedAt + factoryTimeout;
            Assert.True(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt, now, factoryTimeout));
        }

        [Fact]
        public void ShouldReportNow_ManyWindowsLater_KeepsRepeatingOnceMore_NotJustTwice()
        {
            // The fallback must keep going for the life of the outage, not just fire a 2nd time and
            // then stop - simulates 5 consecutive grace-window cycles, each with its own report.
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var factoryTimeout = TimeSpan.FromMilliseconds(150);
            DateTime? lastReportedAt = null;
            var reportCount = 0;
            for (var cycle = 1; cycle <= 5; cycle++)
            {
                var now = detectedAt + TimeSpan.FromTicks(factoryTimeout.Ticks * cycle);
                if (FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt, now, factoryTimeout))
                {
                    reportCount++;
                    lastReportedAt = now;
                }
            }
            Assert.Equal(5, reportCount);
        }
    }
}
