// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

// Every test class here creates RingBufferManager<T> instances sharing the same Meter/
// ActivitySource Name ("RingBufferPlus"). RingBufferObservabilityTests relies on global
// MeterListener/ActivityListener registrations filtered by a unique buffer.name per test,
// but running collections in parallel still means every other class's managers are live
// and emitting on those same global listeners concurrently. Disabling parallelization
// removes that cross-class overlap entirely instead of relying solely on the name filter.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
