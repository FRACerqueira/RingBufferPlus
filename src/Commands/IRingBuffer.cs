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
    /// Represents the commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBuffer<T>
    {
        /// <summary>
        /// Default capacity of ring buffer.
        /// </summary>
        /// <param name="value">Initial capacity.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Capacity(int value);

        /// <summary>
        /// Factory to create an instance in ring buffer.
        /// <br>Executes asynchronously.</br>
        /// </summary>
        /// <param name="value">The handler to factory.</param>
        /// <param name="timeout">The timeout  for build. Default value is 10 seconds.</param>
        /// <param name="idleRetryError">The delay time for retrying when a build fails. Default value is 5 seconds.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Factory(Func<CancellationToken,T> value, TimeSpan? timeout = null, TimeSpan? idleRetryError = null);

        /// <summary>
        /// The Logger
        /// <br>Default value is ILoggerFactory.Create (if any) with category euqal name of ring buffer</br>
        /// </summary>
        /// <param name="value"><see cref="ILogger"/>.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> Logger(ILogger value);

        /// <summary>
        /// Timeout to accquire buffer. Default value is 30 seconds.
        /// </summary>
        /// <param name="value">The timeout for acquiring a value from the buffer.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> AccquireTimeout(TimeSpan value);


        /// <summary>
        /// Extension point to log a error.
        /// <br>Executes asynchronously.</br>
        /// </summary>
        /// <param name="errorhandler">he handler to log error.</param>
        /// <returns><see cref="IRingBuffer{T}"/>.</returns>
        IRingBuffer<T> OnError(Action<ILogger?,RingBufferException> errorhandler);

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

        /// <summary>
        /// Swith to scale definitions commands.
        /// </summary>
        /// <returns><see cref="IRingBufferScaleCapacity{T}"/>.</returns>
        IRingBufferScaleCapacity<T> SwithToScaleDefinitions();

    }
}
