// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the Slave capacity commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferSlaveCapacity
        <T>
    {
        /// <summary>
        /// Extension point when capacity was changed.
        /// <br>Executes asynchronously.</br>
        /// </summary>
        /// <param name="report">The handler to action.</param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferSlaveCapacity<T> ReportScale(Action<ScaleMode, ILogger, RingBufferMetric, CancellationToken> report = null);


        /// <summary>
        /// Minimum capacity.
        /// </summary>
        /// <param name="value">The minimal buffer. Value mus be greater or equal 1</param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferSlaveCapacity<T> MinCapacity(int value);

        /// <summary>
        /// Maximum capacity.
        /// </summary>
        /// <param name="value">The maximum buffer.Value mus be greater or equal <see cref="IRingBuffer{T}.Capacity(int)"/></param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferSlaveCapacity<T> MaxCapacity(int value);

        /// <summary>
        /// Validate and generate RingBufferPlus to service mode.
        /// </summary>
        /// <returns><see cref="IRingBufferService{T}"/>.</returns>
        IRingBufferService<T> Build();

        /// <summary>
        /// Validate and generate RingBufferPlus and warmup with full capacity ready or reaching timeout (default 30 seconds).
        /// </summary>
        /// <param name="fullcapacity">True if Warmup has full capacity, otherwise false.</param>
        /// <param name="timeout">The Timeout to Warmup has full capacity. Default value is 30 seconds.</param>
        /// <returns><see cref="IRingBufferService{T}"/>.</returns>
        IRingBufferService<T> BuildWarmup(out bool fullcapacity, TimeSpan? timeout = null);

    }
}
