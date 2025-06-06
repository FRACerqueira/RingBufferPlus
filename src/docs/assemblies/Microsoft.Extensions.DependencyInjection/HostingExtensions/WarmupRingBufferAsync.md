![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)

### HostingExtensions.WarmupRingBufferAsync&lt;T&gt; method
</br>


#### Warms up with full capacity ready or reaching timeout (default 30 seconds).

```csharp
public static Task WarmupRingBufferAsync<T>(this IHost appbluild, string buffername, 
    CancellationToken? token = default)
```

| parameter | description |
| --- | --- |
| T | Type of buffer. |
| appbluild | The IHost. |
| buffername | The unique name to RingBuffer. |
| token | The CancellationToken. Default value is ApplicationStopping. |

### Exceptions

| exception | condition |
| --- | --- |
| ArgumentNullException | Buffer name null or empty |
| ArgumentNullException | Buffer not found |

### Remarks

It is recommended to use this method in the initialization of the application.

If you do not use the 'Warmup Ring Buffer' command, the first access to buffer servives([`IRingBufferService`](../../RingBufferPlus/IRingBufferService-1.md)) will be Warmup (not recommended)

If the time limit is reached, the task will continue on to another internal task until it reaches the defined capacity.

### See Also

* class [HostingExtensions](../HostingExtensions.md)
* namespace [Microsoft.Extensions.DependencyInjection](../../RingBufferPlus.md)

<!-- DO NOT EDIT: generated by xmldocmd for RingBufferPlus.dll -->
* [Main Index](../../../docindex.md)
