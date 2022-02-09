using Microsoft.Extensions.Logging;
using RingBufferPlus;
using RingBufferPlus.ObjectValues;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RingBufferPlusTest
{
    public class RingBufferBuildTest
    {
        public class MyClassTest
        {
        }


        [Fact]
        public void Should_have_DefautValue_width_max_min_and_not_autoscaler()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .MaxBuffer(20)
                .MinBuffer(8)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.Equal(20, rb.MaximumCapacity);
            Assert.Equal(8, rb.MinimumCapacity);
            Assert.Equal(10, rb.InitialCapacity);
        }

        [Fact]
        public void Should_have_DefautValue_FactoryAync()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .Build();
            Assert.Equal(10, rb.MaximumCapacity);
            Assert.Equal(2, rb.MinimumCapacity);
        }

        [Fact]
        public void Should_have_DefautValue_timespan_HealthCheck_not_has_HealthCheck()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.Equal(DefaultValues.IntervalHealthcheck, rb.IntervalHealthCheck);
            Assert.False(rb.HasHealthCheck);
        }

        [Fact]
        public void Should_have_DefautValue_timespan_HealthCheck_has_HealthCheck()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .Factory((_) => new MyClassTest())
                .HealthCheck((_, _) => true)
                .Build();
            Assert.Equal(DefaultValues.IntervalHealthcheck, rb.IntervalHealthCheck);
            Assert.True(rb.HasHealthCheck);
        }

        [Fact]
        public void Should_have_exception_Create_when_MinScaler_GreaterThan_Initialcapacity()
        {
            var ex = Record.Exception(() =>
            {
                var rb = RingBuffer<MyClassTest>
                    .CreateBuffer()
                    .InitialBuffer(10)
                    .MinBuffer(11)
                    .Build();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_exception_Create_when_MaxScaler_LessThan_Initialcapacity()
        {
            var ex = Record.Exception(() =>
            {
                var rb = RingBuffer<MyClassTest>
                    .CreateBuffer()
                    .InitialBuffer(10)
                    .MaxBuffer(9)
                    .Build();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_exception_Create_when_notfactory()
        {
            var ex = Record.Exception(() =>
            {
                var rb = RingBuffer<MyClassTest>
                    .CreateBuffer()
                    .InitialBuffer(10)
                    .Build();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_accept_width_MaxScaler_with_autoscaler()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .MaxBuffer(20)
                .Factory((_) => new MyClassTest())
                .AutoScaler((_, _) => 10)
                .Build();

            Assert.Equal(20, rb.MaximumCapacity);
            Assert.Equal(2, rb.MinimumCapacity);
            Assert.Equal(10, rb.InitialCapacity);
        }

        [Fact]
        public void Should_have_accept_logprovider()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .Factory((_) => new MyClassTest())
                .AddLogProvider(new LoggerFactory(), RingBufferLogLevel.Information)
                .Build();
            Assert.True(rb.HasLogging);
            Assert.Equal(LogLevel.Information, rb.DefaultLogLevel);
        }

        [Fact]
        public void Should_have_accept__not_logprovider()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.False(rb.HasLogging);
            Assert.Equal(LogLevel.None, rb.DefaultLogLevel);
        }



        [Fact]
        public void Should_have_accept_width_MinScaler_with_autoscaler()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .MinBuffer(5)
                .Factory((_) => new MyClassTest())
                .AutoScaler((_, _) => 10)
                .Build();
            Assert.Equal(10, rb.MaximumCapacity);
            Assert.Equal(5, rb.MinimumCapacity);
        }

        [Fact]
        public void Should_have_accept_width_AliasName()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .AliasName("Test")
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.Equal($"Test", rb.Alias);
        }

        private class PolicyFakeProviderOK
        {
            public static IEnumerable<object> PolicyTestCasesSync
            {
                get
                {
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken,bool>?>(
                            RingBufferPolicyTimeout.MaximumCapacity, null) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?>(
                            RingBufferPolicyTimeout.EveryTime, null) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?>(
                        RingBufferPolicyTimeout.UserPolicy, (_,_) => true) };
                }
            }
            public static IEnumerable<object> PolicyTestCasesASync
            {
                get
                {
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                            RingBufferPolicyTimeout.MaximumCapacity, null) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                            RingBufferPolicyTimeout.EveryTime, null) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                        RingBufferPolicyTimeout.UserPolicy, (_,_) => Task.FromResult(true)) };
                }
            }
        }

        private class PolicyFakeProviderNOK
        {
            public static IEnumerable<object> PolicyTestCasesSync
            {
                get
                {
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?>(
                            RingBufferPolicyTimeout.MaximumCapacity, (_,_) => true) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?>(
                            RingBufferPolicyTimeout.EveryTime, (_,_) => true) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?>(
                        RingBufferPolicyTimeout.UserPolicy, null) };
                }
            }

            public static IEnumerable<object> PolicyTestCasesASync
            {
                get
                {
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                            RingBufferPolicyTimeout.MaximumCapacity, (_,_) => Task.FromResult(true)) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                            RingBufferPolicyTimeout.EveryTime, (_,_) => Task.FromResult(true)) };
                    yield return new object[] { Tuple
                        .Create<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, Task<bool>>?>(
                        RingBufferPolicyTimeout.UserPolicy, null) };
                }
            }
        }

        [Theory]
        [MemberData(nameof(PolicyFakeProviderOK.PolicyTestCasesSync), MemberType = typeof(PolicyFakeProviderOK))]
        public void Should_have_accept_width_PolicyTimeoutAccquire_Sync(Tuple<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?> testcase)
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .SetPolicyTimeout(testcase.Item1, testcase.Item2)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.Equal(testcase.Item1, rb.PolicyTimeout);
            Assert.Equal(testcase.Item2 != null, rb.HasPolicyTimeout);
        }


        [Theory]
        [MemberData(nameof(PolicyFakeProviderNOK.PolicyTestCasesSync), MemberType = typeof(PolicyFakeProviderNOK))]
        public void Should_have_exception_width_PolicyTimeoutAccquire_Sync(Tuple<RingBufferPolicyTimeout, Func<RingBufferMetric, CancellationToken, bool>?> testcase)
        {
            var ex = Record.Exception(() =>
            {
                var rb = RingBuffer<MyClassTest>
                    .CreateBuffer()
                    .InitialBuffer(10)
                    .SetPolicyTimeout(testcase.Item1, testcase.Item2)
                    .Factory((_) => new MyClassTest())
                    .Build();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_accept_width_TimeoutAccquire()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .SetTimeoutAccquire(100, 10)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.Equal(TimeSpan.FromMilliseconds(100), rb.TimeoutAccquire);
            Assert.Equal(TimeSpan.FromMilliseconds(10), rb.IdleAccquire);
        }

        [Fact]
        public void Should_have_accept_width_HealthCheckSync_defaultInterval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .HealthCheck((_, _) => true)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasHealthCheck);
            Assert.Equal(DefaultValues.IntervalHealthcheck, rb.IntervalHealthCheck);
        }

        [Fact]
        public void Should_have_accept_width_HealthCheckSync_Interval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .HealthCheck((_, _) => true)
                .SetIntervalHealthCheck(333)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasHealthCheck);
            Assert.Equal(333, rb.IntervalHealthCheck.TotalMilliseconds);
        }

        [Fact]
        public void Should_have_accept_width_HealthCheckASync_defaultInterval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .HealthCheckAsync((_, _) => Task.FromResult(true))
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasHealthCheck);
            Assert.Equal(DefaultValues.IntervalHealthcheck, rb.IntervalHealthCheck);
        }

        [Fact]
        public void Should_have_accept_width_HealthCheckASync_Interval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .HealthCheck((_, _) => true)
                .SetIntervalHealthCheck(333)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasHealthCheck);
            Assert.Equal(333, rb.IntervalHealthCheck.TotalMilliseconds);
        }

        [Fact]
        public void Should_have_accept_width_AutoScalerSync_defaultInterval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .AutoScaler((_, _) => 10)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasAutoScaler);
            Assert.Equal(DefaultValues.IntervalScaler, rb.IntervalAutoScaler);
        }

        [Fact]
        public void Should_have_accept_width_AutoScalerSync_Interval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .AutoScaler((_, _) => 10)
                .SetIntervalAutoScaler(444,222)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasAutoScaler);
            Assert.Equal(444, rb.IntervalAutoScaler.TotalMilliseconds);
            Assert.Equal(222, rb.WarmupAutoScaler.TotalMilliseconds);
        }

        [Fact]
        public void Should_have_accept_width_AutoScalerASync_defaultInterval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .AutoScalerAsync((_, _) => Task.FromResult(10))
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasAutoScaler);
            Assert.Equal(DefaultValues.IntervalHealthcheck, rb.IntervalHealthCheck);
        }

        [Fact]
        public void Should_have_accept_width_AutoScalerASync_Interval()
        {
            var rb = RingBuffer<MyClassTest>
                .CreateBuffer()
                .InitialBuffer(10)
                .AutoScaler((_, _) => 10)
                .SetIntervalAutoScaler(444,222)
                .Factory((_) => new MyClassTest())
                .Build();
            Assert.True(rb.HasAutoScaler);
            Assert.Equal(444, rb.IntervalAutoScaler.TotalMilliseconds);
            Assert.Equal(222, rb.WarmupAutoScaler.TotalMilliseconds);
        }

    }
}
