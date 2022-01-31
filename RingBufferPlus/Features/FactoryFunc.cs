using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace RingBufferPlus.Features
{
    [ExcludeFromCodeCoverage]
    internal struct FactoryFunc<T>
    {
        public Func<CancellationToken, T>? ItemSync { get; set; }
        public Func<CancellationToken, Task<T>>? ItemAsync { get; set; }
        public bool ExistFuncAsync => ItemAsync != null;
        public bool ExistFuncSync => ItemSync != null;
        public bool ExistFunc => ExistFuncAsync || ExistFuncSync;
    }

}
