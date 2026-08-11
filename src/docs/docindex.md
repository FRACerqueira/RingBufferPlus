![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)

### RingBufferPlus Documentation 
</br>

### Microsoft.Extensions.DependencyInjection namespace

| public type | description |
| --- | --- |
| static class [HostingExtensions](./assemblies/Microsoft.Extensions.DependencyInjection/HostingExtensions.md) | Represents the commands to add RingBufferPlus in ServiceCollection and Warmup. |

### RingBufferPlus namespace

| public type | description |
| --- | --- |
| interface [IRingBufferAutoScaleBuilder&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferAutoScaleBuilder-1.md) | Represents a RingBufferPlus builder committed to an elastic capacity with autoscale-on-fault enabled. |
| interface [IRingBufferBuilder&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferBuilder-1.md) | Represents the entry point to configure and build a RingBufferPlus instance. |
| interface [IRingBufferElasticBuilder&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferElasticBuilder-1.md) | Represents a RingBufferPlus builder committed to an elastic (min/init/max) capacity, producing an [`IRingBufferManualScaleService`](./assemblies/RingBufferPlus/IRingBufferManualScaleService-1.md) unless [`AutoScaleAcquireFault`](./assemblies/RingBufferPlus/IRingBufferElasticBuilder-1/AutoScaleAcquireFault.md) is used. |
| interface [IRingBufferFixedBuilder&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferFixedBuilder-1.md) | Represents a RingBufferPlus builder committed to a fixed capacity. |
| interface [IRingBufferManualScaleService&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferManualScaleService-1.md) | Represents a RingBufferPlus service that can be manually switched between capacities. |
| interface [IRingBufferService&lt;T&gt;](./assemblies/RingBufferPlus/IRingBufferService-1.md) | Represents the commands to RingBufferPlus service. |
| static class [RingBuffer&lt;T&gt;](./assemblies/RingBufferPlus/RingBuffer-1.md) | Represents the RingBufferPlus extensions. |
| static class [RingBufferDefault](./assemblies/RingBufferPlus/RingBufferDefault.md) | Represents the default values for the ring buffer. |
| class [RingBufferValue&lt;T&gt;](./assemblies/RingBufferPlus/RingBufferValue-1.md) | Represents acquired the value in the buffer. |
| enum [ScaleSwitch](./assemblies/RingBufferPlus/ScaleSwitch.md) | Represents Scale switch of RingBufferPlus. |
