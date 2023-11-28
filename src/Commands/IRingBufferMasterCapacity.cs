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
    /// Represents the Master capacity commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferMasterCapacity
        <T>
    {
        /// <summary>
        /// Sampling unit for return buffer-free resource (Average colledted samples).
        /// <br>baseunit/value must be greater or equal than 100ms.</br>
        /// </summary>
        /// <param name="baseunit">The <see cref="TimeSpan"/> interval to colleted samples.Default baseunit is 60 seconds.</param>
        /// <param name="value">Number of samples collected.Default value is baseunit/10. Default value is 60.</param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferMasterCapacity<T> SampleUnit(TimeSpan? baseunit = null,int? value = null);

        /// <summary>
        /// Sampling unit for return buffer-free resource (Average colledted samples).
        /// <br>baseunit/value must be greater or equal than 100ms.</br>
        /// <br>Base unit = The interval to colledted samples. Default is 60 seconds.</br>
        /// </summary>
        /// <param name="value">
        /// Number of samples collected.Default value is baseunit/10. Default value is 60.
        /// <br>Base unit = The interval to colledted samples. Default is 60 seconds.</br>
        /// </param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferMasterCapacity<T> SampleUnit(int? value = null);

        /// <summary>
        /// Extension point when capacity was changed.
        /// <br>Executes asynchronously.</br>
        /// </summary>
        /// <param name="report">The handler to action.</param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferMasterCapacity<T> ReportScale(Action<ScaleMode, ILogger, RingBufferMetric, CancellationToken> report = null);


        /// <summary>
        /// Minimum capacity.
        /// </summary>
        /// <param name="value">The minimal buffer. Value mus be greater or equal 1</param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferScaleMin<T> MinCapacity(int value);

        /// <summary>
        /// Maximum capacity.
        /// </summary>
        /// <param name="value">The maximum buffer.Value mus be greater or equal <see cref="IRingBuffer{T}.Capacity(int)"/></param>
        /// <returns><see cref="IRingBufferMasterCapacity{T}"/>.</returns>
        IRingBufferScaleMax<T> MaxCapacity(int value);

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
