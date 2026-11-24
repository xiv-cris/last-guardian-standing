using System;
using System.Collections.Generic;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Net;

/// <summary>
/// Client side: keeps recent snapshots and replays the match slightly in the past, blending between
/// snapshots so motion stays smooth even though packets arrive unevenly.
/// </summary>
public sealed class SnapshotBuffer
{
    /// <summary>How far behind the newest snapshot we draw, in ticks (8 ticks at 120 Hz = ~67 ms).</summary>
    public const double InterpolationDelayTicks = 8;
    const int Capacity = 64;

    readonly List<GameState> _states = new();
    double _clock = -1;     // estimate of the host's current tick
    int _firedThroughTick = int.MinValue;

    /// <summary>Snapshots from any other match id are ignored.</summary>
    public int MatchId { get; private set; } = -1;

    public GameState? Latest => _states.Count > 0 ? _states[^1] : null;
    public double RenderTick => _clock - InterpolationDelayTicks;

    public void Reset(int matchId)
    {
        _states.Clear();
        _clock = -1;
        _firedThroughTick = int.MinValue;
        MatchId = matchId;
    }

    public void Add(GameState s)
    {
        if (s.MatchId != MatchId) return;
        if (Latest is { } last && s.Tick <= last.Tick) return; // late or duplicate

        _states.Add(s);
        if (_states.Count > Capacity) _states.RemoveAt(0);

        if (_clock < 0 || Math.Abs(_clock - s.Tick) > 60)
            _clock = s.Tick; // first snapshot or a big hiccup: snap
    }

    /// <summary>Moves the local estimate of the host clock forward, gently pulled toward the newest snapshot.</summary>
    public void Advance(double seconds)
    {
        if (_clock < 0 || Latest == null) return;
        _clock += seconds * Protocol.TickRate;
        _clock += (Latest.Tick - _clock) * Math.Min(1.0, seconds * 3.0);
    }

    /// <summary>The state to draw right now. Always a fresh copy, safe to modify.</summary>
    public GameState? Sample()
    {
        if (_states.Count == 0) return null;
        double rt = RenderTick;

        if (rt <= _states[0].Tick) return _states[0].Clone();
        for (int i = _states.Count - 1; i > 0; i--)
        {
            var a = _states[i - 1];
            var b = _states[i];
            if (a.Tick <= rt && rt <= b.Tick)
                return GameState.Interpolate(a, b, (float)((rt - a.Tick) / Math.Max(1, b.Tick - a.Tick)));
        }
        return _states[^1].Clone();
    }

    /// <summary>Events whose snapshot the render time has now reached, so effects line up with what is drawn.</summary>
    public List<SimEvent> TakeDueEvents()
    {
        var due = new List<SimEvent>();
        double rt = RenderTick;
        foreach (var s in _states)
        {
            if (s.Tick <= _firedThroughTick || s.Tick > rt) continue;
            due.AddRange(s.Events);
            _firedThroughTick = s.Tick;
        }
        return due;
    }
}
