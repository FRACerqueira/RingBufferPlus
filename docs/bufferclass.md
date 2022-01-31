# <img align="left" width="100" height="100" src="./images/icon.png"> RingBufferPlus # RingBufferValue

[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Alias**](aliasname.md) |
[current state](currentstate.md)

## Documentation
Ring buffer return class details by Accquire method.

**The return class must execute the "Dispose" after its use.**

### Properties/Methods - (All Properties are ReadOnly)

- State 
	-  the [current state](currentstate.md) class of RingBuffer.
- ElapsedAccquire 
	-  Elapsed time in milliseconds to acquire the item in the buffer
- Alias 
	-  the alias for the Ringbuffer instance.
- SucceededAccquire
	-  Buffer item acquired successfully
- Current
	-  Buffer item.
- Error
	-  Error that occurred when acquisition fails
- Invalidate()
	-  Even with success status the returned object may be in an invalid state. For this scenario, this method tells the component not to return to the Buffer when the operation is finished.

### **Hypothetical Use**
```csharp
using (var ctx = ring.Accquire())
{
   if (ctx.SucceededAccquire)
   {
      try
      {
          //do something
      }
      catch (Exception ex)
      {
         ctx.Invalidate();
      }
   }
   else if (!ctx.State.FailureState)
   {
       //do something different
   }
   else
   {
      Console.WriteLine($"{ctx.Alias} => Error: {ctx.Error}.");
   }
}
```

### Links
[**Main**](index.md#help) | 
[**Apis**](index.md#apis) |
[**Alias**](aliasname.md) |
[current state](currentstate.md)

