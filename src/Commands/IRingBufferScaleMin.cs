// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;

namespace RingBufferPlus
{
    /// <summary>
    /// Represents the MinCapacity commands to RingBufferPlus.
    /// </summary>
    /// <typeparam name="T">Type of buffer.</typeparam>
    public interface IRingBufferScaleMin<T>
    {
        /// <summary>
        /// Condition to scale down to min capacity.
        /// <br>The free resource collected must be greater than or equal to value.</br>
        /// </summary>
        /// <param name="value">
        /// Number to free resource. 
        /// <br>Defaut = Max. (Min = 2, Max = Capacity).</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMin{T}"/>.</returns>
        IRingBufferScaleMin<T> ScaleWhenFreeGreaterEq(int? value = null);

        /// <summary>
        /// Condition to trigger to default capacity (check at foreach accquire).
        /// <br>The free resource collected at aqccquire must be less than or equal to value.</br>
        /// <br>TriggerByAccqWhenFreeLessEq do not used with RollbackWhenFreeLessEq command.</br>
        /// </summary>
        /// <param name="value">
        /// Number to trigger.
        /// <br>Defaut = Max-1 (Min = 2, Max = MinCapacity).</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMin{T}"/>.</returns>
        IRingBufferScaleMin<T> TriggerByAccqWhenFreeLessEq(int? value = null);


        /// <summary>
        /// Condition to scale up to default capacity.
        /// <br>The free resource (averange collected) must be less than or equal to value.</br>
        /// <br>RollbackWhenFreeLessEq do not used with TriggerByAccqWhenFreeLessEq command.</br>
        /// </summary>
        /// <param name="value">
        /// Number of averange collected.
        /// <br>Defaut = Min  (Min = 1, Max = MinCapacity).</br>
        /// </param>
        /// <returns><see cref="IRingBufferScaleMin{T}"/>.</returns>
        IRingBufferScaleMin<T> RollbackWhenFreeLessEq(int? value = null);

        /// <summary>
        /// Maximum capacity.
        /// </summary>
        /// <param name="value">The maximum buffer.</param>
        /// <returns><see cref="IRingBufferScaleMax{T}"/>.</returns>
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