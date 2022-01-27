using System;
using System.Collections;
using System.Threading;

namespace RingBufferPlus.ObjectValues
{
    public struct RingBufferfState
    {
        private readonly int _currentRunning;
        private readonly int _currentAvailable;
        private readonly int _currentCapacity;
        private readonly bool _hassick;

        public RingBufferfState()
        {
            throw new InvalidOperationException($"Invalid Create {nameof(RingBufferfState)}");
        }

        internal RingBufferfState(int currentRunning, int currentAvailable, bool hasSick)
        {
            _currentRunning = currentRunning;
            _currentAvailable = currentAvailable;
            _currentCapacity = currentRunning + currentAvailable;
            _hassick = hasSick;
        }
        public int CurrentAvailable => _currentAvailable;
        public int CurrentRunning => _currentRunning;
        public int CurrentCapacity => _currentCapacity;
        public bool HasSick => _hassick;
    }
}
