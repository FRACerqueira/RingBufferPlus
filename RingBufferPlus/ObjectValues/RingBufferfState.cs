using System;
using System.Diagnostics.CodeAnalysis;

namespace RingBufferPlus.ObjectValues
{
    [ExcludeFromCodeCoverage]
    public struct RingBufferfState
    {
        private readonly int _currentRunning;
        private readonly int _currentAvailable;
        private readonly int _currentCapacity;
        private readonly bool _hassick;
        private readonly int _max;
        private readonly int _min;

        public RingBufferfState()
        {
            throw new InvalidOperationException($"Invalid Create {nameof(RingBufferfState)}");
        }

        internal RingBufferfState(int currentRunning, int currentAvailable,int max,int min, bool hasSick)
        {
            _currentRunning = currentRunning;
            _currentAvailable = currentAvailable;
            _currentCapacity = currentRunning + currentAvailable;
            _max = max;
            _min = min;
            _hassick = hasSick;
        }
        public int CurrentAvailable => _currentAvailable;
        public int CurrentRunning => _currentRunning;
        public int CurrentCapacity => _currentCapacity;
        public int MinimumCapacity => _min;
        public int MaximumCapacity => _max;
        public bool FailureState => _hassick;
    }
}
