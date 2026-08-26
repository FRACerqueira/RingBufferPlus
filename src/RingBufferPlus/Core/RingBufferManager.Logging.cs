// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed partial class RingBufferManager<T>
    {
        #region logging

        private void LogMessage(string message)
        {
            // The IsEnabled(Debug) check is necessary here, not just cosmetic. Without it, every
            // call would still build the interpolated string and a closure even when Debug
            // logging is off - and ProcessTick calls this roughly every SamplesBase/SamplesCount
            // interval (300ms by default) for the whole lifetime of every elastic buffer, not just
            // per scale operation.
            if (Logger is null || !SafeIsEnabled(Logger, LogLevel.Debug)) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            SafeInvokeSink(() => logMessageForDbg(Logger, Name, msg, null));
        }

        private void LogWarning(string message)
        {
            if (Logger is null) return;
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {message} ";
            SafeInvokeSink(() => logMessageFoWrn(Logger, Name, msg, null));
        }

        private void LogError(Exception error)
        {
            if (Logger is null && ErrorHandler is null) return;
            if (ErrorHandler is null)
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name}: {error.Message} ";
                SafeInvokeSink(() => logMessageForErr(Logger!, Name, msg, error));
            }
            else
            {
                SafeInvokeSink(() => ErrorHandler.Invoke(error));
            }
        }

        // A user-supplied Logger/ErrorHandler is untrusted external code: if it throws, that must
        // never be allowed to permanently break the heartbeat pump, leak a pooled item, make
        // DisposeAsync() itself throw, or otherwise escape into an unrelated core operation like
        // WarmupAsync/AcquireAsync. There's nothing further to log about the failure, since the
        // sink itself is what's broken - so this is a silent best-effort swallow, the same "must
        // never throw regardless of how a background pump ended" philosophy DisposeAsync's own
        // comment already states.
        private static void SafeInvokeSink(Action invoke)
        {
            try
            {
                invoke();
            }
            catch
            {
                //ignore: the logging/error sink itself threw - nothing further can be logged about it
            }
        }

        // Same untrusted-external-code rationale as SafeInvokeSink above: a user-supplied Logger
        // is untrusted, and IsEnabled itself can throw (Microsoft.Extensions.Logging's composite
        // Logger aggregates and rethrows provider exceptions, e.g. a provider disposed ahead of
        // this manager during host shutdown). An unguarded IsEnabled(Debug) check here, unlike
        // every other call into Logger/ErrorHandler in this class, could permanently kill
        // _engineTask or _heartbeatTask, since ProcessTick/RunHeartbeatAsync have no catch broad
        // enough to survive it. So a throwing IsEnabled is treated as "not enabled", skipping this
        // one Debug line instead of propagating.
        private static bool SafeIsEnabled(ILogger logger, LogLevel level)
        {
            try
            {
                return logger.IsEnabled(level);
            }
            catch
            {
                return false;
            }
        }

        private static readonly Action<ILogger, string, string, Exception?> logMessageForDbg = LoggerMessage.Define<string, string>(LogLevel.Debug, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageForErr = LoggerMessage.Define<string, string>(LogLevel.Error, 0, "RingBufferManager({Source}) : {Message}");
        private static readonly Action<ILogger, string, string, Exception?> logMessageFoWrn = LoggerMessage.Define<string, string>(LogLevel.Warning, 0, "RingBufferManager({Source}) : {Message}");

        #endregion
    }
}
