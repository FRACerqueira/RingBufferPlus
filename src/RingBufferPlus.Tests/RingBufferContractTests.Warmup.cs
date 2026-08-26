// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using RingBufferPlus.Core;

namespace RingBufferPlus.Tests
{
    public partial class RingBufferContractTests
    {

        // ---------------------------------------------------------------------
        // 1.1 - Concurrent warmup idempotency
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task ConcurrentWarmup_InvokesFactoryExactlyCapacityTimes()
        {
            // Arrange
            var factoryCalls = 0;
            var manager = CreateFixedManager(5, _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(1);
            });

            // Act: many concurrent callers. Warmup uses Lazy<Task>, so idempotency is guaranteed
            // by construction - it doesn't depend on scheduling or a hand-rolled flag check.
            // Task.Run forces real thread-pool parallelism; a plain Task.WhenAll over the async
            // calls directly could otherwise run them sequentially on the calling thread.
            var warmups = Enumerable.Range(0, 20).Select(_ => Task.Run(() => manager.WarmupAsync()));
            await Task.WhenAll(warmups);

            // Assert: the factory must be invoked exactly Capacity times, never duplicated.
            Assert.Equal(5, factoryCalls);

            await manager.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.8 - A factory (or a user item's Dispose) that throws a plain exception must not kill
        // the engine loop or hang later calls to Warmup/Acquire/Switch/Dispose.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_WhenFactoryThrows_PropagatesTheRealException_AndDoesNotHang()
        {
            // Arrange: every factory call throws a plain (non-cancellation) exception.
            var manager = CreateFixedManager(3, _ => throw new InvalidOperationException("boom"));

            // Act: bound the wait, since a faulted engine leaving this TCS unresolved must not
            // hang WarmupAsync forever.
            var warmupTask = manager.WarmupAsync();
            var completed = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(3)));

            // Assert: must complete (not hang) and surface the real exception, not a generic wrapper.
            Assert.Same(warmupTask, completed);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => warmupTask);
            Assert.Equal("boom", ex.Message);

            await manager.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.36-1.38 - A factory can throw OperationCanceledException/TaskCanceledException for its
        // own unrelated reasons - e.g. an HttpClient/gRPC/DB driver's own internal timeout, unrelated
        // to this buffer's _lifetime. That real exception must still surface, not get misread as an
        // ordinary shutdown. This extends the existing "surface the real factory exception on a
        // zero-progress batch" rule to this OperationCanceledException-shaped case too.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_WhenFactoryThrowsOperationCanceledException_PropagatesTheRealException_NotAGenericWrapper()
        {
            var manager = CreateFixedManager(3, _ => throw new TaskCanceledException("factory's own unrelated timeout"));

            var warmupTask = manager.WarmupAsync();
            var completed = await Task.WhenAny(warmupTask, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.Same(warmupTask, completed);
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => warmupTask);
            Assert.Equal("factory's own unrelated timeout", ex.Message);

            await manager.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.13 - An item returned via TurnbackAsync after the manager is already disposed must be
        // disposed itself, not silently dropped - this matters specifically on the
        // ChannelClosedException path (channel already closed).
        // ---------------------------------------------------------------------

        private sealed class DisposableProbe : IDisposable
        {
            private int _disposeCount;
            public int DisposeCount => Volatile.Read(ref _disposeCount);
            public bool TouchedAfterDispose { get; private set; }
            public void Dispose() => Interlocked.Increment(ref _disposeCount);
            public void Touch()
            {
                if (DisposeCount > 0)
                {
                    TouchedAfterDispose = true;
                }
            }
        }

        private sealed class ThrowingOnDisposeProbe : IDisposable
        {
            private volatile bool _throwOnDispose;
            public ThrowingOnDisposeProbe(bool throwOnDispose) => _throwOnDispose = throwOnDispose;
            // Settable post-construction so a test can pick, by identity, which specific acquired
            // instance throws - v6.0.0's bounded-concurrent Creator (ADR001V03) creates a batch's
            // items concurrently, so "the Nth factory call" no longer reliably corresponds to "the
            // Nth item that ends up acquired"; tests that need one specific *acquired* item to
            // throw must flip this after acquiring, not bake it into the factory by call order.
            public bool ThrowOnDispose { set => _throwOnDispose = value; }
            public bool Disposed { get; private set; }
            public void Dispose()
            {
                Disposed = true;
                if (_throwOnDispose)
                {
                    throw new InvalidOperationException("Simulated pooled item Dispose() failure.");
                }
            }
        }

        private sealed class HangingDisposeProbe(ManualResetEventSlim release) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                // Capped at 10s so a broken fix can't actually hang the test process forever - the
                // assertions themselves are what prove the library-level bound (PulseHeartBeat) works.
                await Task.Run(() => release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }
        }

        // A plain synchronous IDisposable, unlike HangingDisposeProbe above - its Dispose() blocks
        // the calling thread directly, with no await point of its own.
        private sealed class HangingSyncDisposeProbe(ManualResetEventSlim release) : IDisposable
        {
            // Capped at 10s so a broken fix can't actually hang the test process forever - the
            // assertions themselves are what prove the library-level bound (PulseHeartBeat) works.
            public void Dispose() => release.Wait(TimeSpan.FromSeconds(10));
        }

        // Hangs past the grace period, then - once released - faults on its way out. Used to land
        // a background dispose fault (the ContinueWith in DisposeOneItemDefensivelyAsync's
        // TimeoutException branch) AFTER _logQueue has already been completed by DisposeAsync's
        // own finally block.
        private sealed class HangingThenThrowingDisposeProbe(ManualResetEventSlim release) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await Task.Run(() => release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                throw new InvalidOperationException("Simulated late dispose failure after grace period.");
            }
        }

        // The composite Logger from Microsoft.Extensions.Logging aggregates and rethrows provider
        // exceptions from IsEnabled itself - a provider disposed ahead of this manager during host
        // shutdown is a realistic way to hit this.
        private sealed class ThrowingIsEnabledLogger : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => throw new ObjectDisposedException("provider");
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Messages { get; } = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (Messages) Messages.Add(message);
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        // ---------------------------------------------------------------------
        // WarmupCoreAsync's very first statement is a LogMessage call ("Starting warmup
        // process."), so a throwing Logger.IsEnabled must not escape synchronously from there -
        // unlike ProcessTick/RunHeartbeatAsync, this path has no earlier guard to catch it.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_WhenLoggerIsEnabledThrows_StillCompletes()
        {
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractThrowingIsEnabledDoesNotBreakWarmup", null);
            var service = await builder
                .Factory(_ => Task.FromResult(1))
                .Logger(new ThrowingIsEnabledLogger())
                .FixedCapacity(2)
                .BuildWarmupAsync();

            Assert.Equal(2, service.CurrentCapacity);

            await service.DisposeAsync();
        }

        // ---------------------------------------------------------------------
        // 1.17 - A failed WarmupAsync() must not permanently brick the instance (ADR011) -
        // caching the failure forever via the underlying Lazy<Task>, with no way to
        // recover except constructing a brand new instance, would be a real problem given every DI
        // guide recommends registering the buffer as a singleton.
        // ---------------------------------------------------------------------

        [Fact]
        [Trait("Category", "Contract")]
        public async Task WarmupAsync_AfterAFailedAttempt_RetriesInsteadOfCachingTheFailureForever()
        {
            var shouldFail = true;
            IRingBufferBuilder<int> builder = new RingBufferBuilder<int>("ContractWarmupRetry", null);
            var service = builder
                .Factory(_ => shouldFail ? throw new InvalidOperationException("factory down") : Task.FromResult(1))
                .FixedCapacity(2)
                .Build();

            var firstEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.NotNull(firstEx);

            // Retry: an explicit second WarmupAsync() call must attempt from scratch, not rethrow
            // the same cached failure. The factory recovers before the retry.
            shouldFail = false;
            var secondEx = await Record.ExceptionAsync(() => service.WarmupAsync());
            Assert.Null(secondEx);

            var acquired = await service.AcquireAsync();
            Assert.True(acquired.Successful);
            await acquired.DisposeAsync();

            await service.DisposeAsync();
        }
    }
}
