// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Acceptance tests for the floor guard (ADR001V03) - see the class remarks on
// FloorGuardDecision for how it is wired into the engine.

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
            // MaxCapacity == Capacity). This must not count as a breach, or the guard would
            // fire constantly.
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
            // MinCapacity == 2 is the smallest value RingBufferBuilder.ValidateBuild allows.
            // The guard must still detect a breach there.
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
            // Deliberately ">=", not ">": a broken factory must be reported as soon as a full
            // FactoryTimeout has passed, not one tick later.
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
            // If `now` is earlier than the breach's own detected-at instant (e.g. a clock glitch
            // or an out-of-order call), that must never look like "elapsed". The negative elapsed
            // time counts as "still within the window", not a larger apparent duration.
            var breachDetectedAt = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
            var now = breachDetectedAt - TimeSpan.FromSeconds(5);
            Assert.False(FloorGuardDecision.HasGraceWindowElapsed(breachDetectedAt, now, TimeSpan.FromSeconds(15)));
        }

        // ---------------------------------------------------------------------
        // ShouldReportNow: the "below minimum" report fires once when the grace window elapses,
        // then again on the same cadence as a fallback. It must never repeat on every single
        // evaluation, and never go silent forever after the first report.
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
            // Without a latch, this call would report again immediately, since the grace window
            // has already elapsed since detection. Once a report has fired, "elapsed since
            // detection" is no longer enough - a fresh grace window must also pass since then.
            var detectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastReportedAt = detectedAt + TimeSpan.FromMilliseconds(150);
            var now = lastReportedAt + TimeSpan.FromMilliseconds(50);
            Assert.False(FloorGuardDecision.ShouldReportNow(detectedAt, lastReportedAt, now, TimeSpan.FromMilliseconds(150)));
        }

        [Fact]
        public void ShouldReportNow_ManyRapidRetriesWithinOneWindow_OnlyTheFirstReports()
        {
            // Simulates several evaluations happening close together, inside one grace window
            // (e.g. a batch of retries completing in quick succession). Only the first should
            // report; the rest must not.
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
            // The fallback must keep repeating for the whole outage, not stop after a second
            // report. Simulates 5 consecutive grace-window cycles, each with its own report.
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
