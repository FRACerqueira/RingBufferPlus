// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Tests
{
    public class RingBufferValueTests
    {
        [Fact]
        public void Constructor_WithParameters_ShouldInitializeProperties()
        {
            var name = "TestBuffer";
            var elapsedTime = TimeSpan.FromSeconds(1);
            var value = 42;
            var succeeded = true;
            static ValueTask turnback(RingBufferValue<int> _) => ValueTask.CompletedTask;

            var ringBufferValue = new RingBufferValue<int>(name, elapsedTime, succeeded, value, turnback);

            Assert.Equal(name, ringBufferValue.Name);
            Assert.Equal(elapsedTime, ringBufferValue.ElapsedTime);
            Assert.Equal(succeeded, ringBufferValue.Successful);
            Assert.Equal(value, ringBufferValue.Current);
        }

        [Fact]
        public void Invalidate_Successful_ShouldSetSkipTurnback()
        {
            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, true, 42, null);

            ringBufferValue.Invalidate();

            Assert.True(ringBufferValue.SkipTurnback);
        }

        [Fact]
        public void Invalidate_Unsuccessful_ShouldNotSetSkipTurnback()
        {
            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, false, 42, null);

            ringBufferValue.Invalidate();

            Assert.False(ringBufferValue.SkipTurnback);
        }

        [Fact]
        public async Task DisposeAsync_ShouldInvokeTurnback()
        {
            var turnbackInvoked = false;
            ValueTask turnback(RingBufferValue<int> _) { turnbackInvoked = true; return ValueTask.CompletedTask; }

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, true, 42, turnback);

            await ringBufferValue.DisposeAsync();

            Assert.True(turnbackInvoked);
        }

        [Fact]
        public async Task DisposeAsync_MultipleTimes_ShouldInvokeTurnbackOnce()
        {
            var turnbackCount = 0;
            ValueTask turnback(RingBufferValue<int> _) { turnbackCount++; return ValueTask.CompletedTask; }

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, true, 42, turnback);

            await ringBufferValue.DisposeAsync();
            await ringBufferValue.DisposeAsync();

            Assert.Equal(1, turnbackCount);
        }

        [Fact]
        public async Task DisposeAsync_Unsuccessful_ShouldStillInvokeTurnbackDelegate()
        {
            // RingBufferValue itself does not gate on Successful - that decision belongs to
            // whoever supplies the turnback delegate (RingBufferManager.TurnbackAsync, which
            // passes null for unsuccessful acquires in practice).
            var turnbackInvoked = false;
            ValueTask turnback(RingBufferValue<int> _) { turnbackInvoked = true; return ValueTask.CompletedTask; }

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, false, 42, turnback);

            await ringBufferValue.DisposeAsync();

            Assert.True(turnbackInvoked);
        }
    }
}
