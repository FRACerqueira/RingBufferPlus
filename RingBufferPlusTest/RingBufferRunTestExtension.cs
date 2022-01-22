using RingBufferPlus;
using RingBufferPlus.ObjectValues;

namespace RingBufferPlusTest
{
    internal static class RingBufferRunTestExtension
    {
        internal static void StopAll<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            ((RingBuffer<T>)ringBuffer).TestWaitStopAllTasks();
        }

        internal static void StopHealthCheck<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            ((RingBuffer<T>)ringBuffer).TestWaitStopHealthCheck();
        }

        internal static void StopReport<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            ((RingBuffer<T>)ringBuffer).TestWaitStopReport();
        }


        internal static void StopAutoScaler<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            ((RingBuffer<T>)ringBuffer).TestWaitStopAutoScaler();
        }

        internal static void TriggerScale<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            ((RingBuffer<T>)ringBuffer).TestTriggerAutoScale();
        }

        internal static RingBufferMetric AutoScalerMetric<T>(this IRunningRingBuffer<T> ringBuffer)
        {
            return ((RingBuffer<T>)ringBuffer).TestMetricAutoScaler();
        }
    }
}
