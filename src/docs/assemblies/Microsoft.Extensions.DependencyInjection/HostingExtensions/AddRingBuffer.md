![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)

### HostingExtensions.AddRingBuffer&lt;T&gt; method
</br>


#### Add RingBuffer in ServiceCollection.

```csharp
public static IServiceCollection AddRingBuffer<T>(this IServiceCollection ServiceCollection, 
    string buffername, Func<IRingBuffer<T>, IServiceProvider, IRingBufferService<T>> userfunc)
```

| parameter | description |
| --- | --- |
| T | Type of buffer. |
| ServiceCollection | The IServiceCollection. |
| buffername | The unique name to RingBuffer. |
| userfunc | The Handler to return the [`IRingBufferService`](../../RingBufferPlus/IRingBufferService-1.md). |

### Return Value

IServiceCollection.

### Exceptions

| exception | condition |
| --- | --- |
| ArgumentNullException | Buffer name null or empty |

### See Also

* interface [IRingBuffer&lt;T&gt;](../../RingBufferPlus/IRingBuffer-1.md)
* interface [IRingBufferService&lt;T&gt;](../../RingBufferPlus/IRingBufferService-1.md)
* class [HostingExtensions](../HostingExtensions.md)
* namespace [Microsoft.Extensions.DependencyInjection](../../RingBufferPlus.md)

<!-- DO NOT EDIT: generated by xmldocmd for RingBufferPlus.dll -->
* [Main Index](../../../docindex.md)
