// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************
//
// Behavioral contract tests for RingBufferManager's Channel-based, single-consumer concurrency
// model (see doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md).
// This file is the engine's acceptance gate.
//
// Notably, 1.3 (concurrent SwitchToAsync) is not "best-effort": because the engine is a single
// sequential consumer, "exactly one accepted caller" is a deterministic guarantee, not a
// probabilistic reproduction of a race.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public partial class RingBufferContractTests
    {
        private static RingBufferManager<int> CreateFixedManager(int capacity, Func<CancellationToken, Task<int>> factory, CancellationToken lifetime = default)
        {
            return new RingBufferManager<int>(lifetime)
            {
                Name = "ContractBuffer",
                Capacity = capacity,
                MinCapacity = capacity,
                MaxCapacity = capacity,
                FactoryTimeout = TimeSpan.FromSeconds(2),
                PulseHeartBeat = TimeSpan.FromSeconds(5),
                SamplesBase = TimeSpan.FromSeconds(5),
                SamplesCount = 5,
                AcquireTimeout = TimeSpan.FromMilliseconds(300),
                Factory = factory
            };
        }

        private static object GetPrivateField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {instance.GetType()}.");
            return field.GetValue(instance)!;
        }
    }
}
