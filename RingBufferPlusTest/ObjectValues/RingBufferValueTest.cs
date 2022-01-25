using RingBufferPlus.ObjectValues;
using System;
using Xunit;

namespace RingBufferPlusTest.ObjectValues
{
    public class RingBufferValueTest
    {
        private int CountActionFake;

        private class MyClassTest
        {
            public MyClassTest()
            {
                MyProperty = 1;
            }
            public int MyProperty { get; set; }
        }

        private void ActionFake(MyClassTest test,bool skip)
        {
            CountActionFake++;
            test.MyProperty = 0;
        }

        [Fact]
        public void Should_have_CorrectValues()
        {
            var inst = new MyClassTest();
            var featureTest = new RingBufferValue<MyClassTest>("Alias", 1, 2, 0,true, new Exception("teste"), inst, ActionFake);
            Assert.Equal(1, featureTest.Available);
            Assert.True(featureTest.SucceededAccquire);
            Assert.Equal("Alias", featureTest.Alias);
            Assert.Equal(0, featureTest.ElapsedTime);
            Assert.NotNull(featureTest.Error);
            Assert.Equal("teste", featureTest.Error.Message);
            Assert.Equal(inst, featureTest.Current);
            Assert.Equal(inst.MyProperty, featureTest.Current.MyProperty);
        }

        [Fact]
        public void Should_have_CorrectDispose_withAction()
        {
            var inst = new MyClassTest();
            using (_ = new RingBufferValue<MyClassTest>("Alias", 1, 2,0, true, new Exception("teste"), inst, ActionFake))
            {
            };
            Assert.Equal(1, CountActionFake);
            Assert.Equal(0, inst.MyProperty);
        }

        [Fact]
        public void Should_have_CorrectDispose_withoutAction()
        {
            var inst = new MyClassTest();
            using (_ = new RingBufferValue<MyClassTest>("Alias", 1, 2,0, true, new Exception("teste"), inst, null))
            {
            };
            Assert.Equal(0, CountActionFake);
            Assert.Equal(1, inst.MyProperty);
        }
    }
}
