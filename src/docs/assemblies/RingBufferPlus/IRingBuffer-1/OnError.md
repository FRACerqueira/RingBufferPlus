![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)

### IRingBuffer&lt;T&gt;.OnError method
</br>


#### Sets the error handler to log errors.

```csharp
public IRingBuffer OnError(Action<ILogger?, Exception> errorHandler)
```

| parameter | description |
| --- | --- |
| errorHandler | The handler to log error. |

### Return Value

[`IRingBuffer`](../IRingBuffer-1.md).

### See Also

* interface [IRingBuffer&lt;T&gt;](../IRingBuffer-1.md)
* namespace [RingBufferPlus](../../RingBufferPlus.md)

<!-- DO NOT EDIT: generated by xmldocmd for RingBufferPlus.dll -->
* [Main Index](../../../docindex.md)
