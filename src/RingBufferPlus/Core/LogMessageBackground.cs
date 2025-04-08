// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.Extensions.Logging;

namespace RingBufferPlus.Core
{
    internal sealed record LogMessageBackground(LogLevel LogLevel, string? Message, Exception? Error);

}
