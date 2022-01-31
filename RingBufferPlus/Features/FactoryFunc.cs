using System;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus.Features
{
    internal struct FactoryFunc<T>
    {
        public Func<CancellationToken, T>? ItemSync { get; set; }
        public Func<CancellationToken, Task<T>>? ItemAsync { get; set; }
        public bool ExistFuncAsync => ItemAsync != null;
        public bool ExistFuncSync => ItemSync != null;
        public bool ExistFunc => ExistFuncAsync || ExistFuncSync;
    }

}
