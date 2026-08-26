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

        // ---------------------------------------------------------------------
        // A LogError call from a hung item's background dispose
        // faulting strictly after DisposeAsync() has already returned must still reach OnError, not
        // be silently dropped. Originally about BackgroundLogger(true)'s own queue-completion
        // timing (that queue no longer exists, ADR007V03) - logging is unconditionally synchronous
        // now, but a late-firing background task calling LogError after the method that started it
        // returned is still a real scenario worth guarding.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task LogError_ForABackgroundDisposalThatFaultsAfterDisposeAsyncReturned_IsStillDelivered_NotSilentlyDropped()
        {
            using var releaseHang = new ManualResetEventSlim();
            var errors = new List<Exception>();

            IRingBufferBuilder<HangingThenThrowingDisposeProbe> builder = new RingBufferBuilder<HangingThenThrowingDisposeProbe>("ContractLateBackgroundDisposalFaultNotDropped", null);
            var service = await builder
                .Factory(_ => Task.FromResult(new HangingThenThrowingDisposeProbe(releaseHang)))
                .Logger(new CapturingLogger())
                .OnError(ex => { lock (errors) errors.Add(ex); })
                .HeartBeat(_ => true, pulse: TimeSpan.FromMilliseconds(200))
                .FixedCapacity(2)
                .BuildWarmupAsync();

            // Returns once the grace period elapses for the hung idle item - DisposeAsync's own
            // finally block has already run to completion by this point.
            await service.DisposeAsync();

            // Only now does the background dispose actually finish, and fault - strictly after
            // DisposeAsync() itself has already returned.
            releaseHang.Set();

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (errors)
                {
                    if (errors.Any(e => e is InvalidOperationException ioe && ioe.Message.Contains("Simulated late dispose failure"))) break;
                }
                await Task.Delay(20);
            }
            lock (errors)
            {
                Assert.Contains(errors, e => e is InvalidOperationException ioe && ioe.Message.Contains("Simulated late dispose failure"));
            }
        }

        // ---------------------------------------------------------------------
        // 1.35 - Same unguarded-callback sweep, same class of bug as elsewhere in it, but in
        // RingBufferBuilder<T> instead of RingBufferManager<T>: ValidateBuild's own LogError(err)
        // calls invoked a throwing OnError with no guard, so the ErrorHandler's own bug replaced
        // the real validation failure Build() was about to throw. Lower severity - no running
        // instance/pool state exists yet at this point - but same fix pattern.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public void Build_WhenOnErrorThrows_StillSurfacesTheRealValidationFailure()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractBuildThrowingOnError", null);
            var fixedBuilder = builder
                .Logger(new CapturingLogger())
                .OnError(_ => throw new InvalidOperationException("user OnError sink bug"))
                .FixedCapacity(2);

            var ex = Assert.Throws<InvalidOperationException>(() => fixedBuilder.Build());
            Assert.Equal("The command Factory is not defined.", ex.Message);
        }
    }
}
