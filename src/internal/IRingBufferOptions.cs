﻿// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus
{
    internal interface IRingBufferOptions<T>
    {
        string Name { get; }
        int Capacity { get; }
        int MinCapacity { get; }
        int MaxCapacity { get; }
        Func<CancellationToken, T> FactoryHandler { get; }
        Func<T,bool> BufferHealthHandler { get; }
        TimeSpan BufferHealtTimeout { get; }
        TimeSpan FactoryTimeout { get; }
        TimeSpan FactoryIdleRetryError { get; }
        ILogger Logger { get; }
        bool HasScaleCapacity { get; }
        Action<ILogger?, RingBufferException> ErrorHandler { get; }
        TimeSpan SampleDelay { get; }
        int SampleUnit { get; }
        TimeSpan ScaleCapacityDelay { get; }
        int? ScaleToMinGreaterEq { get; }
        int? MinRollbackWhenFreeLessEq { get; }
        int? MinTriggerByAccqWhenFreeGreaterEq { get; }
        int? ScaleToMaxLessEq { get; }
        int? MaxRollbackWhenFreeGreaterEq { get; }
        int? MaxTriggerByAccqWhenFreeGreaterEq { get; }
        Action<RingBufferMetric, ILogger?, CancellationToken?> ReportHandler { get; }
        TimeSpan AccquireTimeout { get; }
        IRingBufferSwith SwithFrom { get; }
        IRingBufferSwith SwithTo { get; }
        ScaleMode UserSwithScale { get; }
        bool IsSlave { get; }

    }
}
