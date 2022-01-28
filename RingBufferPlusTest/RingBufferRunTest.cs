using RingBufferPlus;
using RingBufferPlus.Events;
using RingBufferPlus.ObjectValues;
using System;

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RingBufferPlusTest
{
    public class RingBufferRunTest
    {
        private int CountActionFake;

        //start with MyProperty =1 after execute ActionFake  MyProperty =0 and increment CountActionFake
        private class MyClassTest
        {
            public MyClassTest()
            {
                MyProperty = 1;
            }
            public int MyProperty { get; set; }
        }
        private void ActionFake(MyClassTest test)
        {
            CountActionFake++;
            test.MyProperty = 0;
        }


        [Fact]
        public void Should_have_RunOk_with_facSync_default_autoScaler()
        {
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .Factory((_) => new MyClassTest())
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.Equal(rb.Alias, arg.Metric.Alias);
            Assert.Equal(arg.OldCapacity, arg.NewCapacity);
            Assert.Equal(0, arg.Metric.AcquisitionCount);
            Assert.Equal(10, arg.Metric.Avaliable);
            Assert.Equal(rb.IntervalAutoScaler, arg.Metric.CalculationInterval);
            Assert.Equal(10, arg.Metric.Capacity);
            Assert.Equal(0, arg.Metric.ErrorCount);
            Assert.Equal(10, arg.Metric.Maximum);
            Assert.Equal(10, arg.Metric.Minimum);
            Assert.Equal(0, arg.Metric.OverloadCount);
            Assert.Equal(0, arg.Metric.Running);
        }

        [Fact]
        public void Should_have_RunOk_with_facAsync_default_autoScaler()
        {
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.Equal(arg.OldCapacity, arg.NewCapacity);
            Assert.Equal(0, arg.Metric.AcquisitionCount);
            Assert.Equal(rb.Alias, arg.Metric.Alias);
            Assert.Equal(10, arg.Metric.Avaliable);
            Assert.Equal(rb.IntervalAutoScaler, arg.Metric.CalculationInterval);
            Assert.Equal(10, arg.Metric.Capacity);
            Assert.Equal(0, arg.Metric.ErrorCount);
            Assert.Equal(10, arg.Metric.Maximum);
            Assert.Equal(10, arg.Metric.Minimum);
            Assert.Equal(0, arg.Metric.OverloadCount);
            Assert.Equal(0, arg.Metric.Running);
        }

        [Theory]
        [InlineData(10, 20, 5, 15, 15)]
        [InlineData(10, 20, 5, 8, 8)]
        [InlineData(10, 20, 5, 1, 5)]
        [InlineData(10, 20, 5, 25, 20)]
        public void Should_have_RunOk_with_factSync_userautoScaler(int cap, int max, int min, int newtarg, int result)
        {
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(cap)
                .MaxBuffer(max)
                .MinBuffer(min)
                .Factory((_) => new MyClassTest())
                .AutoScaler((_, _) => newtarg)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();
            var metric = rb.AutoScalerMetric();

            Assert.NotNull(arg);
            Assert.Equal(result, arg.NewCapacity);
            Assert.Equal(0, arg.Metric.AcquisitionCount);
            Assert.Equal(rb.Alias, arg.Metric.Alias);
            Assert.True(result <= rb.CurrentState.CurrentCapacity);
            Assert.Equal(rb.IntervalAutoScaler, arg.Metric.CalculationInterval);
            Assert.Equal(0, arg.Metric.ErrorCount);
            Assert.Equal(max, arg.Metric.Maximum);
            Assert.Equal(min, arg.Metric.Minimum);
            Assert.Equal(0, arg.Metric.OverloadCount);
            Assert.Equal(0, arg.Metric.Running);
        }

        [Theory]
        [InlineData(10, 20, 5, 15, 15)]
        [InlineData(10, 20, 5, 8, 8)]
        [InlineData(10, 20, 5, 1, 5)]
        [InlineData(10, 20, 5, 25, 20)]
        public void Should_have_RunOk_with_factAsync_userautoScaler(int cap, int max, int min, int newtarg, int result)
        {
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(cap)
                .MaxBuffer(max)
                .MinBuffer(min)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .AutoScaler((_, _) => newtarg)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();
            var metric = rb.AutoScalerMetric();

            Assert.NotNull(arg);
            Assert.Equal(result, arg.NewCapacity);
            Assert.Equal(0, arg.Metric.AcquisitionCount);
            Assert.Equal(rb.Alias, arg.Metric.Alias);
            Assert.Equal(result, rb.CurrentState.CurrentCapacity);
            Assert.Equal(rb.IntervalAutoScaler, arg.Metric.CalculationInterval);
            Assert.Equal(0, arg.Metric.ErrorCount);
            Assert.Equal(max, arg.Metric.Maximum);
            Assert.Equal(min, arg.Metric.Minimum);
            Assert.Equal(0, arg.Metric.OverloadCount);
            Assert.Equal(0, arg.Metric.Running);

        }

        [Fact]
        public void Should_have_exception_width_factSync_exception()
        {
            var ex = Record.Exception(() =>
            {
                var rb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .Factory((_) => throw new Exception())
                    .Build()
                    .Run();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_notexception_width_factSync_exception_autoscaler()
        {
            var ex = Record.Exception(() =>
            {
                var triggerException = false;
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .MaxBuffer(12)
                    .Factory((_) =>
                    {
                        if (triggerException)
                        {
                            throw new Exception();
                        }
                        return new MyClassTest();
                    })
                    .AutoScaler((_, _) => 12)
                    .Build();

                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    completion.Set();
                };

                var rb = brb.Run();
                triggerException = true;
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();
            });
            Assert.Null(ex);
        }

        [Fact]
        public void Should_have_notexception_width_factAsync_exception_autoscaler()
        {
            RingBufferAutoScaleEventArgs? arg = null;
            var ex = Record.Exception(() =>
            {
                var triggerException = false;
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .MaxBuffer(12)
                    .FactoryAsync((_) =>
                    {
                        if (triggerException)
                        {
                            throw new Exception();
                        }
                        return Task.FromResult(new MyClassTest());
                    })
                    .AutoScaler((_, _) => 12)
                    .Build();


                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    arg = e;
                    completion.Set();
                };

                var rb = brb.Run();
                triggerException = true;
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();
            });
            Assert.Null(ex);
        }


        [Fact]
        public void Should_have_exception_when_factSync_lessthan_minimum()
        {
            var ex = Record.Exception(() =>
            {
                var cnt = 0;
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .MinBuffer(8)
                    .Factory((_) =>
                    {
                        if (cnt > 3)
                        {
                            throw new Exception();
                        }
                        Interlocked.Increment(ref cnt);
                        return new MyClassTest();
                    })
                    .Build()
                    .Run();
            });
            Assert.NotNull(ex);
        }


        [Fact]
        public void Should_have_exception_when_factAsync_lessthan_minimum()
        {
            var ex = Record.Exception(() =>
            {
                var cnt = 0;
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .MinBuffer(8)
                    .FactoryAsync((_) =>
                    {
                        if (cnt > 3)
                        {
                            throw new Exception();
                        }
                        Interlocked.Increment(ref cnt);
                        return Task.FromResult(new MyClassTest());
                    })
                    .Build()
                    .Run();
            });
            Assert.NotNull(ex);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_report()
        {
            RingBufferMetric? runmetric = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .Factory((_) => new MyClassTest())
                .MetricsReport((metric, _) => { runmetric = metric; })
                .DefaultIntervalReport(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopReport();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(runmetric);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_report_exception()
        {
            int runmetric = 0;
            RingBufferAutoScaleEventArgs? arg = null;
            var ex = Record.Exception(() =>
            {
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .Factory((_) => new MyClassTest())
                    .MetricsReport((metric, _) =>
                    {
                        runmetric++;
                        throw new Exception();
                    })
                    .DefaultIntervalReport(10)
                    .Build();


                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    arg = e;
                    ((RingBuffer<MyClassTest>)inst).StopReport();
                    completion.Set();
                };

                var rb = brb.Run();
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();

            });
            Assert.Null(ex);
            Assert.NotNull(arg);
            Assert.True(runmetric > 1);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_reportAsync()
        {
            RingBufferMetric? runmetric = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .Factory((_) => new MyClassTest())
                .MetricsReportAsync(async (metric, _) =>
                {
                    runmetric = metric;
                    await Task.CompletedTask;
                })
                .DefaultIntervalReport(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopReport();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(runmetric);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_reportAsync_exception()
        {
            var runmetric = 0;
            RingBufferAutoScaleEventArgs? arg = null;
            var ex = Record.Exception(() =>
            {
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .Factory((_) => new MyClassTest())
                    .MetricsReportAsync(async (metric, _) =>
                    {
                        runmetric++;
                        await Task.FromException(new Exception());
                    })
                    .DefaultIntervalReport(10)
                    .Build();

                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    arg = e;
                    ((RingBuffer<MyClassTest>)inst).StopReport();
                    completion.Set();
                };

                var rb = brb.Run();
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();
            });
            Assert.Null(ex);
            Assert.NotNull(arg);
            Assert.True(runmetric > 1);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_report()
        {
            RingBufferMetric? runmetric = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .MetricsReport((metric, _) => { runmetric = metric; })
                .DefaultIntervalReport(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopReport();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(runmetric);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_report_exception()
        {
            var runmetric = 0;
            RingBufferAutoScaleEventArgs? arg = null;
            var ex = Record.Exception(() =>
            {
                var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .MetricsReport((metric, _) =>
                {
                    runmetric++;
                    throw new Exception();
                })
                .DefaultIntervalReport(100)
                .Build();


                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    arg = e;
                    ((RingBuffer<MyClassTest>)inst).StopReport();
                    completion.Set();
                };

                var rb = brb.Run();
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();
            });
            Assert.Null(ex);
            Assert.NotNull(arg);
            Assert.True(runmetric > 1);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_reportAsync()
        {
            RingBufferMetric? runmetric = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .MetricsReportAsync(async (metric, _) =>
                {
                    runmetric = metric;
                    await Task.CompletedTask;
                })
                .DefaultIntervalReport(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopReport();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(runmetric);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_reportAsync_exception()
        {
            var runmetric = 0;
            RingBufferAutoScaleEventArgs? arg = null;
            var ex = Record.Exception(() =>
            {
                var brb = RingBuffer<MyClassTest>
                    .CreateBuffer(10)
                    .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                    .MetricsReportAsync(async (metric, _) =>
                    {
                        runmetric++;
                        await Task.FromException(new Exception());
                    })
                    .DefaultIntervalReport(100)
                    .Build();


                var completion = new ManualResetEvent(false);

                brb.AutoScalerCallback += (inst, e) =>
                {
                    arg = e;
                    ((RingBuffer<MyClassTest>)inst).StopReport();
                    completion.Set();
                };

                var rb = brb.Run();
                rb.TriggerScale();
                completion.WaitOne();
                rb.StopAutoScaler();

            });
            Assert.Null(ex);
            Assert.NotNull(arg);
            Assert.True(runmetric > 1);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_hc()
        {
            MyClassTest? hc = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .Factory((_) => new MyClassTest())
                .HealthCheck((buff, _) =>
                {
                    hc = buff;
                    return true;
                })
                .DefaultIntervalHealthCheck(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopHealthCheck();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(hc);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_hc()
        {
            MyClassTest? hc = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .HealthCheck((buff, _) =>
                {
                    hc = buff;
                    return true;
                })
                .DefaultIntervalHealthCheck(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopHealthCheck();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(hc);
        }

        [Fact]
        public void Should_have_RunOk_with_facSync_and_hcAsync()
        {
            MyClassTest? hc = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .Factory((_) => new MyClassTest())
                .HealthCheckAsync(async (buff, _) =>
                {
                    hc = buff;
                    return await Task.FromResult(true);
                })
                .DefaultIntervalHealthCheck(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopHealthCheck();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(hc);
        }

        [Fact]
        public void Should_have_RunOk_with_facASync_and_hcAsync()
        {
            MyClassTest? hc = null;
            var brb = RingBuffer<MyClassTest>
                .CreateBuffer(10)
                .FactoryAsync((_) => Task.FromResult(new MyClassTest()))
                .HealthCheckAsync(async (buff, _) =>
                {
                    hc = buff;
                    return await Task.FromResult(true);
                })
                .DefaultIntervalHealthCheck(100)
                .Build();

            RingBufferAutoScaleEventArgs? arg = null;

            var completion = new ManualResetEvent(false);

            brb.AutoScalerCallback += (inst, e) =>
            {
                arg = e;
                ((RingBuffer<MyClassTest>)inst).StopHealthCheck();
                completion.Set();
            };

            var rb = brb.Run();
            rb.TriggerScale();
            completion.WaitOne();
            rb.StopAutoScaler();

            Assert.NotNull(arg);
            Assert.NotNull(hc);
        }
    }
}
