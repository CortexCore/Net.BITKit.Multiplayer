using System;
using System.Diagnostics;

namespace BITKit.Multiplayer
{
/// <summary>One room's unscaled monotonic seconds. Client values estimate Host time using RTT/2.</summary>
public sealed class NetworkTime
{
    private readonly object _gate = new object();
    private readonly bool _authority;
    private readonly Func<double> _clock;
    private readonly double _origin;
    private bool _active = true, _synchronized;
    private double _offset, _last, _sent, _bestRtt = double.PositiveInfinity, _sampleAt;
    private long _sequence, _pending;
    internal NetworkTime(bool authority, Func<double>? clock = null)
    {
        _authority = authority; _clock = clock ?? Monotonic;
        _origin = _clock(); _synchronized = authority;
    }
    private static double Monotonic() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private double Local => _clock() - _origin;
    public bool IsSynchronized { get { lock (_gate) return _active && _synchronized; } }
    public double RoundTripTime { get { lock (_gate) return double.IsPositiveInfinity(_bestRtt) ? 0 : _bestRtt; } }
    public double Time
    {
        get { lock (_gate) { if (!_active || !_synchronized) return _last; return _last = Math.Max(_last, Local + _offset); } }
    }
    // Familiar Unity-style spelling, without a global current room.
    public double time => Time;
    internal long BeginSample()
    {
        lock (_gate)
        {
            if (!_active || _authority || _sequence == long.MaxValue) return 0;
            if (_pending != 0 && Local - _sent < 5) return 0;
            _sent = Local; _pending = ++_sequence; return _pending;
        }
    }
    internal void AcceptSample(long sequence, double hostTime)
    {
        lock (_gate)
        {
            if (!_active || _authority || sequence == 0 || sequence != _pending) return;
            if (double.IsNaN(hostTime) || double.IsInfinity(hostTime) || hostTime < 0) return;
            double now = Local, rtt = now - _sent;
            if (rtt < 0 || rtt > 5) return;
            _pending = 0;
            // Prefer low-queueing samples; refresh the selection window to track drift.
            if (!_synchronized || rtt <= _bestRtt || now - _sampleAt > 15)
            { _offset = hostTime + rtt * .5 - now; _bestRtt = rtt; _sampleAt = now; _synchronized = true; }
        }
    }
    internal void Stop()
    {
        lock (_gate)
        { if (_active && _synchronized) _last = Math.Max(_last, Local + _offset); _active = false; _pending = 0; }
    }
}
}
