// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Benchmarks
{
    // Round 2 (Desempenho, v6 pre-release audit): confirms with a real number that
    // RingBufferManager.LogMessage's `IsEnabled(LogLevel.Debug)` guard (added this round; it was
    // missing before) actually avoids the interpolated-string + closure cost on every Monitor
    // tick when a Logger is configured but Debug is not enabled - none of the other 3 new
    // benchmarks added this round configure `.Logger(...)` at all, so none of them exercised this
    // path either before or after the fix.
    //
    // This mirrors LogMessage's body verbatim (guard, then DateTime.Now + string interpolation,
    // then a closure passed to a LoggerMessage.Define-style delegate) rather than calling the
    // private method itself, the same technique MonitorTickCostBenchmarks already uses for
    // ProcessTick's sample-window logic. Logger/Name are read from *instance fields*, not method
    // parameters, deliberately matching RingBufferManager's own shape: the closure below captures
    // `this` (for the fields) and `msg` (a local declared after the guard), so the compiler-
    // generated display class is allocated after the early return, not before it - the same as
    // production. A parameter-captured logger would force the display class to be allocated at
    // method entry, before the guard runs, and understate the guard's real saving.
    [MemoryDiagnoser]
    public class MonitorTickLogGuardBenchmarks
    {
        private const string Message = "Monitor tick: demand=10, target=8 equals current capacity 8 - no scale.";

        private static readonly Action<ILogger, string, string, Exception?> LogDelegate =
            LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({Source}) : {Message}");

        private ILogger? _logger;
        private const string Name = "BenchmarkBuffer";

        [Params("DebugEnabled", "DebugDisabled")]
        public string Mode { get; set; } = "";

        [GlobalSetup]
        public void Setup() => _logger = new FixedEnabledLogger(enabled: Mode == "DebugEnabled");

        // Guard added this round (Round 2): `if (Logger is null || !Logger.IsEnabled(LogLevel.Debug)) return;`
        [Benchmark(Baseline = true)]
        public void LogMessage_WithGuard() => LogMessageWithGuard();

        // Pre-fix behavior (Round 1): `if (Logger is null) return;` only - no IsEnabled check, so
        // this always pays the format + closure cost once any Logger is configured, regardless of
        // whether Debug is enabled.
        [Benchmark]
        public void LogMessage_WithoutGuard() => LogMessageWithoutGuard();

        private void LogMessageWithGuard()
        {
            if (_logger is null || !_logger.IsEnabled(LogLevel.Debug)) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {Message} ";
            SafeInvokeSink(() => LogDelegate(_logger, Name, msg, null));
        }

        private void LogMessageWithoutGuard()
        {
            if (_logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {Message} ";
            SafeInvokeSink(() => LogDelegate(_logger, Name, msg, null));
        }

        private static void SafeInvokeSink(Action invoke)
        {
            try { invoke(); }
            catch { /* ignore, mirrors RingBufferManager.SafeInvokeSink */ }
        }

        // Minimal ILogger stand-in: IsEnabled returns a fixed value. Log() is a no-op - this
        // measures the guard's own saving (avoided string format + closure alloc), not the cost
        // of a real logging provider's Debug-enabled write path, which is provider-dependent and
        // out of scope here.
        private sealed class FixedEnabledLogger(bool enabled) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => enabled;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // LoggerMessage.Define-generated delegates already check IsEnabled before calling
                // this, so reaching here with it disabled would mean the guard didn't work.
            }
        }
    }
}
