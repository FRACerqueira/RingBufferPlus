// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the MaxCapacity commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferScaleMax<T>
    {

        /// <summary>
        /// Condition to scale up  to max capacity.
        /// <br>The free resource collected must be less than or equal to value.</br>
        /// </summary>
        /// <param name="value">
        /// Number to trigger.
        /// <br>The free resource collected must be less than or equal to value.</br>
        /// <br>Default = Min (Min =  2, Max = Capacity).</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMax{T}"/></returns>
        IRingBufferScaleMax<T> ScaleWhenFreeLessEq(int? value = null);

        /// <summary>
        /// Condition to scale down to default capacity.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// <br>RollbackWhenFreeGreaterEq do not used with TriggerByAccqWhenFreeGreaterEq command.</br>
        /// </summary>
        /// <param name="value">
        /// Number to trigger.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// <br>Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity).</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMax{T}"/></returns>
        IRingBufferScaleMax<T> RollbackWhenFreeGreaterEq(int? value = null);

        /// <summary>
        /// Condition to scale down to default capacity foreach accquire.
        /// <br>The free resource collected at aqccquire must be greater than or equal to value.</br>
        /// <br>TriggerByAccqWhenFreeGreaterEq do not used with RollbackWhenFreeGreaterEq command.</br>
        /// </summary>
        /// <param name="value">
        /// Number to trigger.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// <br>Default = Min (Min = MaxCapacity-Capacity, Max = MaxCapacity)</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMax{T}"/></returns>
        IRingBufferScaleMax<T> TriggerByAccqWhenFreeGreaterEq(int? value = null);

        /// <summary>
        /// Minimum capacity.
        /// </summary>
        /// <param name="value">The minimum buffer.</param>
        /// <returns><see cref="IRingBufferScaleMin{T}"/>.</returns>
        IRingBufferScaleMin<T> MinCapacity(int value);

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