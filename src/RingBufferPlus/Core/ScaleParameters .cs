// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

namespace RingBufferPlus.Core
{
    internal sealed record ScaleParameters(ScaleSwitch? Scale, ScaleSwitch? Origin, int Quantity, CancellationToken Token);
}
