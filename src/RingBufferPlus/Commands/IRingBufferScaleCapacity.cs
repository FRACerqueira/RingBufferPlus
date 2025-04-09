// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the scale capacity commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferScaleCapacity<T> : IRingBufferBuild<T>
    {
        /// <summary>
        /// Sets the minimum capacity.
        /// </summary>
        /// <remarks>
        /// If the minimum and maximum capacity are equal, autoscaling will be ignored.
        /// </remarks>
        /// <param name="value">The minimal buffer capacity. Value must be greater than or equal to 2.</param>
        /// <returns>An instance of <see cref="IRingBufferScaleCapacity{T}"/>.</returns>
        IRingBufferScaleCapacity<T> MinCapacity(int value);

        /// <summary>
        /// Sets the maximum capacity.
        /// </summary>
        /// <remarks>
        /// If the minimum and maximum capacity are equal, autoscaling will be ignored.
        /// </remarks>
        /// <param name="value">The maximum buffer capacity. Value must be greater than or equal to the minimum capacity.</param>
        /// <returns>An instance of <see cref="IRingBufferScaleCapacity{T}"/>.</returns>
        IRingBufferScaleCapacity<T> MaxCapacity(int value);

        /// <summary>
        /// Sets acquisition/Switch lock when running scaleUp/ScaleDown.
        /// </summary>
        /// <param name="value">True to acquisition lock.Default true</param>
        /// <returns></returns>
        IRingBufferScaleCapacity<T> LockWhenScaling(bool value = true);

        /// <summary>
        /// Sets the condition to autoscale (scale up) capacity when an acquire fault occurs. The Manually change scale will always return false if autoscale is enabled.
        /// </summary>
        /// <remarks>
        /// The scale-up process is executed when the failure threshold defined by <paramref name="numberOfFaults"/> is reached.
        /// The scaling down process is performed based on the initial capacity, minimum capacity or maximum capacity when the number of available buffers is greater than a value.
        /// <para>
        /// The scaledownwhen it is at minimum capacity is calculated using the formula:  Minimum capacity - 2. If the value is less than 1 the value will be 1
        /// </para>
        /// <para>
        /// The scaledown when it is at initial capacity is calculated using the formula: Initial capacity - Minimum capacity  + 2
        /// </para>
        /// <para>
        /// The scaledown when it is at maximum capacity is calculated using the formula: Maximum capacity - Initial capacity  + 2
        /// </para>
        /// <para>
        /// The scaledown process is executed when the following values ​​are reached after calculating the median of the samples(param.ref. int) within TimeSpan). <see cref="IRingBuffer{T}.ScaleTimer(int?, TimeSpan?)"/>. />.
        /// </para>
        /// <para>
        /// The autoscale (scale up or down) has timeout and uses the param.ref. TimeSpan of <see cref="IRingBuffer{T}.ScaleTimer(int?, TimeSpan?)"/>. When the timeout is reached, the operation is undone.
        /// </para>
        /// </remarks>
        /// <param name="numberOfFaults">Number of faults to trigger the scale-up. Default is 1 (after first fault).</param>
        /// <returns>An instance of <see cref="IRingBufferScaleCapacity{T}"/>.</returns>
        IRingBufferScaleCapacity<T> AutoScaleAcquireFault(byte numberOfFaults = 1);
    }
}
