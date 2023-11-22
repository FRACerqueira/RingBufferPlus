// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Threading;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the commands to RingBufferPlus service.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IRingBufferService<T> : IDisposable
    {
        /// <summary>
        /// Unique name to RingBuffer.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Action to read read counters (available/unavailable/for creation)
        /// </summary>
        void Counters(Action<int, int, int> counters);

        /// <summary>
        /// Default capacity of ring buffer.
        /// </summary>
        int Capacity { get; }

        /// <summary>
        /// Minimum capacity.
        /// </summary>
        int MinCapacity { get; }

        /// <summary>
        /// Maximum capacity.
        /// </summary>
        int MaxCapacity { get; }

        /// <summary>
        /// The timeout  for build. Default value is 10 seconds.
        /// </summary>
        TimeSpan FactoryTimeout { get; }

        /// <summary>
        /// The delay time for retrying when a build fails. Default value is 30 seconds.
        /// </summary>
        TimeSpan FactoryIdleRetry { get; }

        /// <summary>
        /// If ring buffer hscapacity to scale
        /// </summary>
        bool ScaleCapacity { get; }

        /// <summary>
        /// The <see cref="TimeSpan"/> interval to colleted samples.Default baseunit is 60 seconds.
        /// </summary>
        TimeSpan SampleUnit { get; }

        /// <summary>
        /// Number of samples collected.Default value is baseunit/10. Default value is 60.
        /// </summary>
        int SamplesCount { get; }

        /// <summary>
        /// Condition to scale down to min capacity.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// </summary>
        int? ScaleToMin { get; }

        /// <summary>
        /// Condition to scale up to default capacity.
        /// <br>The free resource (averange collected) must be less than or equal to value.</br>
        /// </summary>
        int? RollbackFromMin { get; }

        /// <summary>
        /// Condition to trigger to default capacity (check at foreach accquire).
        /// <br>The free resource collected at aqccquire must be less than or equal to value.</br>
        /// </summary>
        int? TriggerFromMin { get; }

        /// <summary>
        /// Condition to scale up  to max capacity.
        /// <br>The free resource collected must be less than or equal to value.</br>
        /// </summary>
        int? ScaleToMax { get; }


        /// <summary>
        /// Condition to scale down to default capacity.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// </summary>
        int? RollbackFromMax { get; }

        /// <summary>
        /// Condition to scale down to default capacity foreach accquire.
        /// <br>The free resource collected at aqccquire must be greater than or equal to value.</br>
        /// </summary>
        int? TriggerFromMax { get; }


        /// <summary>
        /// Timeout to accquire buffer. Default value is 30 seconds.
        /// </summary>
        TimeSpan AccquireTimeout { get; }


        /// <summary>
        /// Try Accquire value on buffer.
        /// <br>Will wait for a buffer item to become available or timeout (default 30 seconds).</br>
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/>.</param>
        /// <returns><see cref="RingBufferValue{T}"/>.</returns>
        RingBufferValue<T> Accquire(CancellationToken? cancellation = null);

    }
}
