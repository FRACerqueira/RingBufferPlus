![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)

### RingBufferPlus assembly
</br>

### Microsoft.Extensions.DependencyInjection namespace

| public type | description |
| --- | --- |
| static class [HostingExtensions](./Microsoft.Extensions.DependencyInjection/HostingExtensions.md) | Represents the commands to add RingBufferPlus in ServiceCollection and Warmup. |

### RingBufferPlus namespace

| public type | description |
| --- | --- |
| interface [IRingBufferBuilder&lt;T&gt;](./RingBufferPlus/IRingBufferBuilder-1.md) | Represents the entry point to configure and build a RingBufferPlus instance. |
| interface [IRingBufferElasticBuilder&lt;T&gt;](./RingBufferPlus/IRingBufferElasticBuilder-1.md) | Represents a RingBufferPlus builder committed to an elastic (min/init/max) capacity, producing an [`IRingBufferManualScaleService`](./RingBufferPlus/IRingBufferManualScaleService-1.md). |
| interface [IRingBufferFixedBuilder&lt;T&gt;](./RingBufferPlus/IRingBufferFixedBuilder-1.md) | Represents a RingBufferPlus builder committed to a fixed capacity. |
| interface [IRingBufferManualScaleService&lt;T&gt;](./RingBufferPlus/IRingBufferManualScaleService-1.md) | Represents a RingBufferPlus service that can be manually pinned to a capacity. |
| interface [IRingBufferService&lt;T&gt;](./RingBufferPlus/IRingBufferService-1.md) | Represents the commands to RingBufferPlus service. |
| static class [RingBuffer&lt;T&gt;](./RingBufferPlus/RingBuffer-1.md) | Represents the RingBufferPlus extensions. |
| static class [RingBufferDefault](./RingBufferPlus/RingBufferDefault.md) | Represents the default values for the ring buffer. |
| class [RingBufferValue&lt;T&gt;](./RingBufferPlus/RingBufferValue-1.md) | Represents acquired the value in the buffer. |
| enum [ScaleSwitch](./RingBufferPlus/ScaleSwitch.md) | Represents Scale switch of RingBufferPlus. |

### See Also
* [Main Index](../docindex.md)
