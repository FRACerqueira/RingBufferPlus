using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace RingBufferPlus.ObjectValues
{
    [ExcludeFromCodeCoverage]
    public struct NaturalTimer
    {
        private DateTimeOffset? _startRef;
        private DateTimeOffset? _endRef;
        private bool _running;
        private double _totalRef;

        public double TotalMilliseconds => Elapsed.TotalMilliseconds;
        public double TotalSeconds => Elapsed.TotalSeconds;
        public double TotalMinutes => Elapsed.TotalMinutes;
        public double TotalHours => Elapsed.TotalHours;
        public TimeSpan Duration => Elapsed.Duration();
        public int Days => Elapsed.Days;
        public int Hours => Elapsed.Hours;
        public int Minutes => Elapsed.Minutes;
        public int Seconds => Elapsed.Seconds;
        public int Milliseconds => Elapsed.Milliseconds;

        public TimeSpan Elapsed
        {
            get
            {
                if (_startRef == null)
                {
                    return TimeSpan.Zero;
                }
                else if (_endRef != null)
                {
                    return (_endRef.Value - _startRef.Value).Add(TimeSpan.FromMilliseconds(_totalRef));
                }
                return (DateTimeOffset.UtcNow.DateTime - _startRef.Value).Add(TimeSpan.FromMilliseconds(_totalRef));
            }
        }
        public static bool Delay(TimeSpan time, CancellationToken? cancellationToken = null)
        {
            return Delay(time, null, cancellationToken);
        }

        public static bool Delay(long timemileseconds, CancellationToken? cancellationToken = null)
        {
            return Delay(TimeSpan.FromMilliseconds(timemileseconds), null, cancellationToken);
        }

        public static bool Delay(long timemileseconds, Func<bool>? cancfunc, CancellationToken? cancellationToken)
        {
            return Delay(TimeSpan.FromMilliseconds(timemileseconds), cancfunc, cancellationToken);
        }

        public static bool Delay(TimeSpan time, Func<bool>? cancfunc, CancellationToken? cancellationToken)
        {
            if (time.TotalMilliseconds == 0)
            {
                return false;
            }
            else if (time.TotalMilliseconds == 1)
            {
                Thread.Sleep(1);
                return false;
            }

            CancellationToken localtoken = CancellationToken.None;
            if (cancellationToken.HasValue)
            {
                localtoken = cancellationToken.Value;
            }
            var sw = new NaturalTimer();
            sw.Start();
            try
            {
                while (sw.TotalMilliseconds < time.TotalMilliseconds)
                {
                    if (localtoken.IsCancellationRequested || (cancfunc?.Invoke() ?? false))
                    {
                        return true;
                    }
                    Thread.Sleep(1);
                }
            }
            finally
            {
                sw.Stop();
            }
            return false;
        }

        public NaturalTimer()
        {
            _startRef = null;
            _endRef = null;
            _running = false;
            _totalRef = 0;
        }

        public void Start()
        {
            if (!_running)
            {
                if (_endRef.HasValue)
                {
                    _totalRef += (_endRef.Value - _startRef.Value).TotalMilliseconds;
                }
                _running = true;
                _startRef = DateTimeOffset.UtcNow.DateTime;
                _endRef = null;
            }
        }

        public void ReStart()
        {
            _running = true;
            _startRef = DateTimeOffset.UtcNow.DateTime;
            _endRef = null;
            _totalRef = 0;
        }

        public void Stop()
        {
            if (_running)
            {
                _running = false;
                _endRef = DateTimeOffset.UtcNow.DateTime;
            }
        }

    }
}
