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
            static void turnback(RingBufferValue<int> _) { }

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
        public void Dispose_ShouldInvokeTurnback()
        {
            bool turnbackInvoked = false;
            void turnback(RingBufferValue<int> _) => turnbackInvoked = true;

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, true, 42, turnback);

            ringBufferValue.Dispose();

            Assert.True(turnbackInvoked);
        }

        [Fact]
        public void Dispose_MultipleTimes_ShouldInvokeTurnbackOnce()
        {
            int turnbackCount = 0;
            void turnback(RingBufferValue<int> _) => turnbackCount++;

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, true, 42, turnback);

            ringBufferValue.Dispose();
            ringBufferValue.Dispose();

            Assert.Equal(1, turnbackCount);
        }

        [Fact]
        public void Dispose_Unsuccessful_ShouldInvokeTurnback()
        {
            bool turnbackInvoked = false;
            void turnback(RingBufferValue<int> _) => turnbackInvoked = true;

            var ringBufferValue = new RingBufferValue<int>("TestBuffer", TimeSpan.Zero, false, 42, turnback);

            ringBufferValue.Dispose();

            Assert.True(turnbackInvoked);
        }
    }
}
