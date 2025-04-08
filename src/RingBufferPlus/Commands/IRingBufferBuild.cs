// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{

    /// <summary>
    ///  Provides methods to build and configure a RingBufferPlus instance.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferBuild<T>
    {
        /// <summary>
        /// Validates and generates RingBufferPlus to service mode.
        /// </summary>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>An instance of <see cref="IRingBufferService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">Invalid configuration of RingBuffer</exception>
        IRingBufferService<T> Build(CancellationToken cancellation = default);

        /// <summary>
        /// Validates and generates RingBufferPlus and warms up with full capacity ready.
        /// </summary>
        /// <remarks>
        /// It is recommended to use this method in the initialization of the application.
        /// </remarks>
        /// <param name="cancellation">The <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an instance of <see cref="IRingBufferService{T}"/>.</returns>
        /// <exception cref="InvalidOperationException">The RingBuffer did not reach initial capacity(TimeSpan Timeout of ScaleTimer)</exception>
        Task<IRingBufferService<T>> BuildWarmupAsync(CancellationToken cancellation = default);

   }
}
