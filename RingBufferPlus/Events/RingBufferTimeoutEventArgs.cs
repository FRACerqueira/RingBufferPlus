using RingBufferPlus.ObjectValues;
using System;

namespace RingBufferPlus.Events
{
    public class RingBufferTimeoutEventArgs : EventArgs
    {

        private RingBufferTimeoutEventArgs()
        {
        }
        internal RingBufferTimeoutEventArgs(string alias, long elapsedtime, long timeout, RingBufferState state)
        {
            Alias = alias;
            ElapsedTime = elapsedtime;
            Timeout = timeout;
            State = state;
        }
        public RingBufferState State { get; set; }
        public long ElapsedTime { get; }
        public long Timeout { get; }
        public string Alias { get; }
    }
}
