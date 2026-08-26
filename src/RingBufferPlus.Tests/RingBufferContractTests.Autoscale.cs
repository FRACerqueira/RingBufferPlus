// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public partial class RingBufferContractTests
    {

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ElasticAutoscale_WhenTriggeredScaleUpFactoryThrows_EngineSurvives_AndAutoscaleRecovers()
        {
            // Arrange: init capacity 4, min 2 (different values, so the scale-up target formula
            // isn't a no-op). The backlog-reactive signal is always active for elastic pools
            // (ADR001V03/ADR007V03) - no toggle needed. The factory only throws while "throwing"
            // is true.
            var throwing = false;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractFaultTriggeredScaleThrows", null);
            var service = builder
                .Factory(_ => throwing ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .ElasticCapacity(2, 6, 4, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            // Exhaust the pool so the next caller starts waiting and triggers the backlog-reactive
            // signal immediately.
            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();
            var held3 = await service.AcquireAsync();
            var held4 = await service.AcquireAsync();

            throwing = true;
            var faultedTask = service.AcquireAsync().AsTask();
            var faultedCompleted = await Task.WhenAny(faultedTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(faultedTask, faultedCompleted);
            Assert.False((await faultedTask).Successful);

            // Give the engine a moment to process the backlog-triggered scale-up (posted fire-and-forget).
            await Task.Delay(300);

            // Assert: capacity did not move (the scale-up's factory call failed), but the engine
            // must still be alive and autoscale still functional afterward.
            Assert.True(service.IsInitCapacity);

            // Recover the factory: autoscale must still work. Backlog-reactive requests exactly
            // the net gap (ADR001V03: proportional, not a coarse jump to MaxCapacity), so with one
            // caller waiting at a time this grows capacity by 1 per successful batch - keep
            // triggering waits (each either times out, or succeeds and immediately consumes the
            // just-created item, keeping the pool empty for the next one) until MaxCapacity.
            throwing = false;
            var growthProbes = new List<RingBufferValue<int>>();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < deadline)
            {
                var probe = await service.AcquireAsync();
                if (probe.Successful)
                {
                    growthProbes.Add(probe);
                }
            }
            Assert.True(service.IsMaxCapacity);

            foreach (var probe in growthProbes)
            {
                await probe.DisposeAsync();
            }
            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await held3.DisposeAsync();
            await held4.DisposeAsync();

            var disposeTask = service.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(disposeTask, disposeCompleted);
            await disposeTask;
        }

        // ---------------------------------------------------------------------
        // 1.15 - Autoscale-on-fault must never be permanently disabled by a legal configuration -
        // initialCapacity == minCapacity must not make the scale-up target formula pick a no-op
        // (target == current) regardless of how many times a fault fires or how healthy the
        // factory is.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ElasticAutoscale_WhenInitialCapacityEqualsMinCapacity_StillScalesUp()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractAutoScaleInitEqualsMin", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 1, TimeSpan.FromSeconds(5))
                .AcquireTimeout(TimeSpan.FromMilliseconds(200))
                .Build();
            await service.WarmupAsync();

            var held1 = await service.AcquireAsync();
            var held2 = await service.AcquireAsync();

            // Pool is empty - this caller starts waiting and triggers the backlog-reactive signal
            // immediately (ADR001V03). Whether this specific acquire ends up succeeding or timing
            // out is not the point - the target formula (CurrentCapacity + gap, capped at
            // MaxCapacity) has no comparison against Capacity/MinCapacity at all, so that failure
            // mode cannot recur here, but the broader "init == min must not block scale-up"
            // property is still worth keeping.
            _ = await service.AcquireAsync();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (service.IsInitCapacity && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsInitCapacity, "Expected the waiting caller to trigger a scale-up away from the initial (== minimum) capacity.");

            await held1.DisposeAsync();
            await held2.DisposeAsync();
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.29 - With initialCapacity == 2 (the minimum legal value), the margin formula must not
        // collapse to currentCapacity itself, which would make scale-down from above initial
        // capacity mathematically unreachable regardless of position - including exactly at
        // MaxCapacity, not just off-tier. Fixed by capping the margin at currentCapacity - 1.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ElasticAutoscale_WithMinimumLegalInitialCapacity_StillScalesDownFromMaxCapacity()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMinimumInitialCapacityScaleDown", null);
            var service = builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 6, 2, 3, TimeSpan.FromMilliseconds(600))
                .AcquireTimeout(TimeSpan.FromMilliseconds(150))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 2; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - 4 concurrent waiters trigger the backlog-reactive signal
            // (ADR001V03), growing capacity 2 -> 6 (MaxCapacity), possibly via more than one
            // successive batch. The Monitor's own scale-down path is unconditionally active for
            // this elastic pool - no toggle to enable it (ADR001V03/ADR007V03).
            var waiterTasks = Enumerable.Range(0, 4).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            var scaleUpDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!service.IsMaxCapacity && DateTime.UtcNow < scaleUpDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.IsMaxCapacity, "Expected the fault-triggered scale-up to reach max capacity.");

            // Every waiter must settle (served or timed out), and any served one disposed, before
            // releasing `held` below - otherwise a leaked never-disposed item would keep the pool
            // from ever becoming genuinely fully idle.
            var waiterResults = await Task.WhenAll(waiterTasks);
            foreach (var result in waiterResults)
            {
                if (result.Successful)
                {
                    await result.DisposeAsync();
                }
            }

            // Release everything so the pool becomes fully idle - scale-down from here must be
            // reachable no matter how idle the pool is, even though initialCapacity == 2 could
            // otherwise make the margin mathematically unreachable at the exact maximum capacity.
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.IsMaxCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.False(service.IsMaxCapacity, "Expected scale-down from MaxCapacity to still be reachable when initialCapacity is the minimum legal value (2), instead of being stuck there forever.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Monitor (ADR001V03/ADR003V03): the deadband call-site wiring (not part of
        // AutoScaleMonitor.EvaluateTarget itself, which AutoScaleMonitorTests.cs already covers in
        // isolation) is what stops the Monitor from re-evaluating its own noise into perpetual
        // motion. Under demand that never changes, capacity should settle once and then stop -
        // the same oscillation-freedom property the validated simulation measured (48 reversals in
        // 300 ticks without a deadband, 1 with it).
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Monitor_UnderSteadyDemand_ConvergesAndStopsChanging_NoOscillation()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMonitorNoOscillation", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 20, 8, 4, TimeSpan.FromMilliseconds(800))
                .BuildWarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Let the Monitor converge from the initial steady demand (4 in use out of 8) first,
            // then sample capacity repeatedly over a further steady window - demand never changes
            // throughout, so once converged it must not keep drifting tick after tick.
            await Task.Delay(2000);
            var samples = new List<int>();
            for (var i = 0; i < 10; i++)
            {
                samples.Add(service.CurrentCapacity);
                await Task.Delay(200);
            }

            // Direction-reversal count, not just "few distinct values" (which a genuine 5,8,5,8,...
            // thrash would still pass) - mirrors the validated simulation's own CountOscillations.
            var reversals = 0;
            var lastDirection = 0;
            for (var i = 1; i < samples.Count; i++)
            {
                var delta = samples[i] - samples[i - 1];
                if (delta == 0)
                {
                    continue;
                }
                var direction = delta > 0 ? 1 : -1;
                if (lastDirection != 0 && direction != lastDirection)
                {
                    reversals++;
                }
                lastDirection = direction;
            }
            Assert.True(reversals == 0, $"Expected capacity to have settled under steady demand, not keep reversing direction. Observed sequence: {string.Join(",", samples)}.");

            foreach (var value in held)
            {
                await value.DisposeAsync();
            }
            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // Monitor (ADR001V03/ADR003V03): same reachability-cap bug class as the old median
        // algorithm had, reproduced in a new shape. MonitorDeadband defaults to 3;
        // a buffer whose Capacity-to-MinCapacity span is smaller than that (here, 4 to 2 - span 2)
        // would otherwise never be able to scale down to its own floor via the Monitor, however
        // idle it becomes, because |target(2) - CurrentCapacity(4)| = 2 < deadband(3) blocks it
        // forever. The fix caps the deadband to the maximum delta actually reachable in the
        // direction being asked for, mirroring AutoScaleDecision's own Math.Min(threshold,
        // currentCapacity - 1) cap for exactly the same reason.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task Monitor_WhenSpanIsSmallerThanTheDeadband_StillReachesMinCapacityWhenIdle()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMonitorDeadbandReachability", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .ElasticCapacity(2, 8, 4, 4, TimeSpan.FromMilliseconds(800))
                .BuildWarmupAsync();

            Assert.Equal(4, service.CurrentCapacity);

            // Fully idle the whole time - no held items, no waiters. If MonitorDeadband(3) is not
            // capped to this buffer's own scale-down span (4 - 2 = 2), CurrentCapacity is
            // mathematically stuck at 4 forever regardless of how long this waits.
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (service.CurrentCapacity > 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.Equal(2, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // ProcessTick's decision to NOT scale
        // (target already equals CurrentCapacity, or the change is
        // within MonitorDeadband) left no trace anywhere. This buffer stays fully idle at its own
        // MinCapacity/target the whole time, so the Monitor's target should converge to (and stay
        // at) CurrentCapacity almost immediately, making the "no scale" tick log reliably reachable.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task MonitorTick_WhenTargetEqualsCurrentCapacity_LogsTheNoScaleDecision()
        {
            var logger = new CapturingLogger();
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractMonitorTickNoScaleLog", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(logger)
                .ElasticCapacity(2, 5, 2, 4, TimeSpan.FromMilliseconds(800))
                .BuildWarmupAsync();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            var found = false;
            while (DateTime.UtcNow < deadline)
            {
                lock (logger.Messages)
                {
                    if (logger.Messages.Any(m => m.Contains("Monitor tick") && m.Contains("no scale")))
                    {
                        found = true;
                        break;
                    }
                }
                await Task.Delay(50);
            }
            Assert.True(found, "Expected a Debug-level Monitor tick log explaining why it did not scale.");

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.26 - A fault-triggered scale-up that only partially succeeds (tolerated failures) can
        // land off-tier, strictly between Capacity and MaxCapacity. Idleness there must still
        // eventually trigger a scale-down, not get stuck at that off-tier capacity forever.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ElasticAutoscale_PartialScaleUpLandsOffTier_StillEventuallyScalesDown()
        {
            // Backlog-reactive (ADR001V03) is self-driving: an unserved waiting caller keeps
            // re-triggering EvaluateBacklogReactive until it is either served or times out - it
            // never "gives up partway" the way a single fixed-quantity batch can. So the landing
            // capacity here isn't fully deterministic even with a bounded number of waiters:
            // _waitingCount can be re-read as "still waiting" slightly after a served caller's own
            // continuation decrements it, occasionally dispatching one extra small batch (a known,
            // accepted approximation that never risks exceeding MaxCapacity - see
            // EvaluateBacklogReactive's own remarks). MaxCapacity is set generously far from
            // Capacity here so this test still reliably lands off-tier (strictly below MaxCapacity)
            // instead of asserting an exact value.
            var callCount = 0;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractOffTierScaleDown", null);
            var service = builder
                .Factory(_ =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    if (n == 5)
                    {
                        // The very first scale-up attempt fails genuinely once (tolerated via
                        // maxConsecutiveFactoryFailures: 1); every other call - warmup and every
                        // later scale-up attempt - succeeds.
                        throw new InvalidOperationException("Simulated transient factory failure.");
                    }
                    return Task.FromResult(1);
                }, TimeSpan.FromSeconds(5), maxConsecutiveFactoryFailures: 1)
                .ElasticCapacity(2, 20, 4, 3, TimeSpan.FromMilliseconds(600))
                .AcquireTimeout(TimeSpan.FromSeconds(1))
                .Build();
            await service.WarmupAsync();

            var held = new List<RingBufferValue<int>>();
            for (var i = 0; i < 4; i++)
            {
                held.Add(await service.AcquireAsync());
            }

            // Pool is empty - 2 concurrent waiters trigger the backlog-reactive signal
            // (ADR001V03). This test's own off-tier scale-down check depends on the Monitor's
            // scale-down path (ADR003V03), unconditionally active for this elastic pool - no toggle
            // to enable it (ADR001V03/ADR007V03).
            var waiterTasks = Enumerable.Range(0, 2).Select(_ => service.AcquireAsync().AsTask()).ToArray();

            // Capacity must both move away from its initial value (a genuine failure happened and
            // backlog reacted) and settle (stop changing for a short, quiet window) before reading
            // it as "landed" - otherwise a still-in-flight extra round (see the class remarks in
            // RingBufferContractTests.cs) could be read as final when it is not.
            var landedOffTierDeadline = DateTime.UtcNow.AddSeconds(5);
            int landedCapacity;
            while (true)
            {
                landedCapacity = service.CurrentCapacity;
                await Task.Delay(150);
                if (landedCapacity > 4 && landedCapacity == service.CurrentCapacity)
                {
                    break;
                }
                Assert.True(DateTime.UtcNow < landedOffTierDeadline, $"Expected capacity to move away from initial (4) and settle within the deadline. Last observed: {service.CurrentCapacity}.");
            }
            Assert.True(landedCapacity > 4 && landedCapacity < 20, $"Expected an off-tier landing strictly between Capacity(4) and MaxCapacity(20). Actual: {landedCapacity}.");
            // Ties the off-tier landing to the injected failure itself, not just to "capacity grew
            // somehow": n == 5 throws exactly once ever (callCount only increases), so every
            // factory invocation beyond the items actually landed in the pool is that one lost
            // attempt. A factory that never failed would have callCount == landedCapacity exactly,
            // which this rules out.
            Assert.Equal(landedCapacity + 1, Volatile.Read(ref callCount));

            // Every waiter must settle (served or timed out), and any served one disposed, before
            // releasing `held` below - otherwise a leaked never-disposed item would keep the pool
            // from ever becoming genuinely fully idle, the scale-down-eligible condition.
            var waiterResults = await Task.WhenAll(waiterTasks);
            foreach (var result in waiterResults)
            {
                if (result.Successful)
                {
                    await result.DisposeAsync();
                }
            }
            foreach (var value in held)
            {
                await value.DisposeAsync();
            }

            var scaleDownDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.CurrentCapacity == landedCapacity && DateTime.UtcNow < scaleDownDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(service.CurrentCapacity < landedCapacity, $"Expected the off-tier capacity ({landedCapacity}) to still be eligible for scale-down once idle, instead of being stuck there forever. Actual: {service.CurrentCapacity}");

            await service.DisposeAsync();
        }
    }
}
