using RingBufferPlus.Internals;
using System;
using System.Threading;

namespace RingBufferPlus.Features
{
    internal class CircuitBreaker<T>
    {
        private FactoryFunc<T> _factory;
        private readonly CancellationToken _cancellationToken;
        public CircuitBreaker(FactoryFunc<T> factory, CancellationToken canelationtoken)
        {
            _factory = factory;
            _cancellationToken = canelationtoken;
        }

        internal ValueException<bool> IsCloseCircuit(Func<bool> racecondition)
        {
            if (racecondition.Invoke())
            {
                return new ValueException<bool>(true);
            }
            Exception tex = null;
            if (_factory.ExistFuncSync)
            {
                try
                {
                    var buff = _factory.ItemSync(_cancellationToken);
                    if (buff is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    tex = ex;
                }
            }
            else
            {
                try
                {
                    var buff = _factory.ItemAsync(_cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    if (buff is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    tex = ex;
                }
            }
            return new ValueException<bool>(tex == null, tex);
        }
    }
}
