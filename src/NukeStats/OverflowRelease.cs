using System.Collections.Generic;

namespace NukeStats
{
    // 1.9.2: flood guard E's release valve for ONE stuck player.
    //
    // Guard E vetoes every buffer-full disconnect (Mirage DisconnectReason 5) because a flood fills everyone's send
    // buffer at once and the game would drop the whole lobby. But when a single player's connection stops taking data,
    // the veto only kept him frozen until Mirage's own timeout dropped him anyway (one player on PvE 10-12: 22,075 failed
    // sends in 33 s, every one of them an exception line, then a reconnect). This lets that one disconnect through once
    // his buffer has been full ON ITS OWN for Flood.SingleVictimReleaseSeconds in a row, so he drops and can rejoin
    // straight away. If any other connection overflowed in the last few seconds it is treated as a flood and absorbed
    // exactly as before, and the lone spell has to start again.
    //
    // Pure logic with the time passed in (so it can be checked outside the game), thread-safe (locked), no Unity calls.
    internal static class OverflowRelease
    {
        internal const double GapSeconds = 1.0;     // a full buffer fails sends many times a second; a 1 s gap = it drained
        internal const double OthersSeconds = 3.0;  // another connection overflowing this recently = a flood, keep absorbing
        const double ForgetSeconds = 30.0;          // runs untouched this long are dropped

        sealed class Run { public double start, last; public bool released; }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<object, Run> _runs = new Dictionary<object, Run>(new SameObject());
        static readonly object _lock = new object();
        static double _nextPrune;
        static readonly double _tickSeconds = 1.0 / System.Diagnostics.Stopwatch.Frequency;

        // a steady clock that any thread may read (Unity's Time.time is main-thread only)
        internal static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * _tickSeconds;

        // Called for every buffer-full disconnect. true = let it through (this player is stuck on his own), false = absorb.
        // firstRelease is true only on the call that first lets this player go (log it once); stuckFor = how long he was stuck.
        internal static bool ShouldRelease(object victim, double now, double releaseAfter, out bool firstRelease, out double stuckFor)
        {
            firstRelease = false;
            stuckFor = 0;
            if (victim == null || releaseAfter <= 0) return false;
            lock (_lock)
            {
                if (now >= _nextPrune)
                {
                    _nextPrune = now + 5.0;
                    List<object> old = null;
                    foreach (var kv in _runs)
                    {
                        if (now - kv.Value.last > ForgetSeconds) (old ??= new List<object>()).Add(kv.Key);
                    }
                    if (old != null) foreach (var k in old) _runs.Remove(k);
                }
                if (!_runs.TryGetValue(victim, out var r) || now - r.last > GapSeconds)
                {
                    r = new Run { start = now };    // a new unbroken run of failed sends
                    _runs[victim] = r;
                }
                r.last = now;
                if (r.released) return true;       // already let go: let the rest of his failed sends through too
                foreach (var kv in _runs)
                {
                    // a player already let go is no sign of a flood; anyone else overflowing just now is
                    if (!ReferenceEquals(kv.Key, victim) && !kv.Value.released && now - kv.Value.last <= OthersSeconds)
                    {
                        r.start = now;              // a flood: absorb, and the lone spell starts again
                        return false;
                    }
                }
                stuckFor = now - r.start;
                if (stuckFor < releaseAfter) return false;
                r.released = true;
                firstRelease = true;
                return true;
            }
        }

        // for the check script: forget everything
        internal static void Reset()
        {
            lock (_lock) { _runs.Clear(); _nextPrune = 0; }
        }
    }
}
