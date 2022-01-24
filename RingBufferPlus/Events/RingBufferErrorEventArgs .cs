using System;

namespace RingBufferPlus.Events
{
    public class RingBufferErrorEventArgs : EventArgs
    {
        private RingBufferErrorEventArgs()
        {
        }
        internal RingBufferErrorEventArgs(string alias, Exception? ex)
        {
            Alias = alias;
            Error = ex;
        }
        public string Alias { get; }
        public Exception? Error { get; }
    }
}
