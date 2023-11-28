// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Threading;

namespace RingBufferPlus
{
    internal interface IRingBufferCallback
    {
        void CallBackMaster(IRingBufferSwith value);
        SemaphoreSlim SemaphoremasterSlave { get; }
        string Name { get; }
        bool IsSlave { get; }

    }
}
