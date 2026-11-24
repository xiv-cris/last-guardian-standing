using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Net;

public static class Protocol
{
    /// <summary>Bump whenever anything sent over the network changes. Host and clients must match.</summary>
    public const int Version = 1;
    public const int DefaultPort = 7777;
    public const int MaxClients = 3;
    public const float TickRate = 120f;          // must match physics/common/physics_ticks_per_second
    public const int SnapshotEveryTicks = 2;     // 60 snapshots per second
}

public struct PlayerState
{
    public ControllerKind Kind;
    public int Team;
    public int Lives;
    public bool Eliminated;
    public float Lateral;

    public readonly bool IsActive => Kind != ControllerKind.Empty && !Eliminated;
}

public struct TeamState
{
    public int Lives;
    public bool Eliminated;
    public int RoundWins;
    public int MemberCount;
}

public struct BallState
{
    public int Id;
    public Vector2 Pos;
    public Vector2 Vel;
    public float SpawnTimer;
    public int LastHitter;      // side index, or -1

    public readonly bool IsLive => SpawnTimer <= 0f;
}

/// <summary>
/// Everything needed to draw one moment of a match. The renderer only ever looks at this, whether the
/// match runs locally or arrives over the network as snapshots.
/// </summary>
public sealed class GameState
{
    const byte Magic = 0x4C; // 'L'

    public int MatchId;
    public int Tick;
    public MatchPhase Phase;
    public float PhaseTimer;
    public int Round;
    public int RoundWinner = -1;
    public int MatchWinner = -1;
    public bool IsPractice;

    public TeamMode TeamMode;
    public TeamLayout ResolvedLayout;
    public LivesMode LivesMode;
    public BallMode BallMode;
    public int Lives;
    public int RoundsToWin;
    public int MaxBalls;

    public PlayerState[] Players = new PlayerState[4];
    public TeamState[] Teams = Array.Empty<TeamState>();
    public List<BallState> Balls = new();

    /// <summary>Events since the previous snapshot (sounds, flashes, shake).</summary>
    public List<SimEvent> Events = new();

    public bool SharedLives => LivesMode == LivesMode.TeamPool && TeamMode != TeamMode.FreeForAll;
    public float SpawnDelay => BallMode == BallMode.Single ? Simulation.SingleSpawnDelay : Simulation.ChaosSpawnDelay;

    public static GameState Capture(Simulation sim, int matchId, IEnumerable<SimEvent> events)
    {
        var s = new GameState
        {
            MatchId = matchId,
            Tick = sim.Tick,
            Phase = sim.Phase,
            PhaseTimer = sim.PhaseTimer,
            Round = sim.Round,
            RoundWinner = sim.RoundWinner,
            MatchWinner = sim.MatchWinner,
            IsPractice = sim.IsPractice,
            TeamMode = sim.Config.TeamMode,
            ResolvedLayout = sim.ResolvedLayout,
            LivesMode = sim.Config.LivesMode,
            BallMode = sim.Config.BallMode,
            Lives = sim.Config.Lives,
            RoundsToWin = sim.Config.RoundsToWin,
            MaxBalls = sim.MaxBalls,
            Teams = new TeamState[sim.Teams.Count],
        };

        for (int i = 0; i < 4; i++)
        {
            var p = sim.Players[i];
            s.Players[i] = new PlayerState { Kind = p.Kind, Team = p.Team, Lives = p.Lives, Eliminated = p.Eliminated, Lateral = p.Lateral };
        }
        for (int t = 0; t < sim.Teams.Count; t++)
        {
            var team = sim.Teams[t];
            s.Teams[t] = new TeamState { Lives = team.Lives, Eliminated = team.Eliminated, RoundWins = team.RoundWins, MemberCount = team.Members.Count };
        }
        foreach (var b in sim.Balls)
        {
            if (b.Dead) continue;
            s.Balls.Add(new BallState
            {
                Id = b.Id, Pos = b.Pos, Vel = b.Vel, SpawnTimer = MathF.Max(0f, b.SpawnTimer),
                LastHitter = b.LastHitter is GoalSide side ? (int)side : -1,
            });
        }
        s.Events.AddRange(events);
        return s;
    }

    public GameState Clone()
    {
        var c = (GameState)MemberwiseClone();
        c.Players = (PlayerState[])Players.Clone();
        c.Teams = (TeamState[])Teams.Clone();
        c.Balls = new List<BallState>(Balls);
        c.Events = new List<SimEvent>(Events);
        return c;
    }

    /// <summary>
    /// A state between two snapshots. Discrete things (lives, phase, which balls exist) come from the older
    /// one; positions are blended. Balls that teleported (respawn) are not blended.
    /// </summary>
    public static GameState Interpolate(GameState a, GameState b, float t)
    {
        var r = a.Clone();
        r.Events.Clear();

        for (int i = 0; i < 4; i++)
            r.Players[i].Lateral = a.Players[i].Lateral + (b.Players[i].Lateral - a.Players[i].Lateral) * t;

        if (a.Phase == b.Phase)
            r.PhaseTimer = a.PhaseTimer + (b.PhaseTimer - a.PhaseTimer) * t;

        for (int i = 0; i < r.Balls.Count; i++)
        {
            var ball = r.Balls[i];
            foreach (var next in b.Balls)
            {
                if (next.Id != ball.Id) continue;
                if (Vector2.DistanceSquared(ball.Pos, next.Pos) < 150f * 150f)
                    ball.Pos = Vector2.Lerp(ball.Pos, next.Pos, t);
                ball.SpawnTimer += (next.SpawnTimer - ball.SpawnTimer) * t;
                r.Balls[i] = ball;
                break;
            }
        }
        return r;
    }

    // ------------------------------------------------------------------ wire format

    public byte[] Serialize()
    {
        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(MatchId);
        w.Write(Tick);
        w.Write((byte)Phase);
        w.Write(PhaseTimer);
        w.Write((short)Round);
        w.Write((sbyte)RoundWinner);
        w.Write((sbyte)MatchWinner);
        w.Write(IsPractice);
        w.Write((byte)TeamMode);
        w.Write((byte)ResolvedLayout);
        w.Write((byte)LivesMode);
        w.Write((byte)BallMode);
        w.Write((byte)Lives);
        w.Write((byte)RoundsToWin);
        w.Write((byte)MaxBalls);

        foreach (var p in Players)
        {
            w.Write((byte)p.Kind);
            w.Write((byte)p.Team);
            w.Write((short)p.Lives);
            w.Write(p.Eliminated);
            w.Write(p.Lateral);
        }

        w.Write((byte)Teams.Length);
        foreach (var t in Teams)
        {
            w.Write((short)t.Lives);
            w.Write(t.Eliminated);
            w.Write((byte)t.RoundWins);
            w.Write((byte)t.MemberCount);
        }

        w.Write((byte)Balls.Count);
        foreach (var b in Balls)
        {
            w.Write(b.Id);
            w.Write(b.Pos.X); w.Write(b.Pos.Y);
            w.Write(b.Vel.X); w.Write(b.Vel.Y);
            w.Write(b.SpawnTimer);
            w.Write((sbyte)b.LastHitter);
        }

        int eventCount = Math.Min(Events.Count, 255);
        w.Write((byte)eventCount);
        for (int i = 0; i < eventCount; i++)
        {
            var e = Events[i];
            w.Write((byte)e.Type);
            w.Write((sbyte)(e.Side is GoalSide s ? (int)s : -1));
            w.Write((sbyte)e.Team);
            w.Write(e.Pos.X); w.Write(e.Pos.Y);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Returns null for anything malformed instead of throwing.</summary>
    public static GameState? Deserialize(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            if (r.ReadByte() != Magic) return null;

            var s = new GameState
            {
                MatchId = r.ReadInt32(),
                Tick = r.ReadInt32(),
                Phase = (MatchPhase)r.ReadByte(),
                PhaseTimer = r.ReadSingle(),
                Round = r.ReadInt16(),
                RoundWinner = r.ReadSByte(),
                MatchWinner = r.ReadSByte(),
                IsPractice = r.ReadBoolean(),
                TeamMode = (TeamMode)r.ReadByte(),
                ResolvedLayout = (TeamLayout)r.ReadByte(),
                LivesMode = (LivesMode)r.ReadByte(),
                BallMode = (BallMode)r.ReadByte(),
                Lives = r.ReadByte(),
                RoundsToWin = r.ReadByte(),
                MaxBalls = r.ReadByte(),
            };

            for (int i = 0; i < 4; i++)
            {
                s.Players[i] = new PlayerState
                {
                    Kind = (ControllerKind)r.ReadByte(),
                    Team = r.ReadByte(),
                    Lives = r.ReadInt16(),
                    Eliminated = r.ReadBoolean(),
                    Lateral = r.ReadSingle(),
                };
            }

            s.Teams = new TeamState[r.ReadByte()];
            for (int t = 0; t < s.Teams.Length; t++)
            {
                s.Teams[t] = new TeamState
                {
                    Lives = r.ReadInt16(),
                    Eliminated = r.ReadBoolean(),
                    RoundWins = r.ReadByte(),
                    MemberCount = r.ReadByte(),
                };
            }

            int balls = r.ReadByte();
            for (int i = 0; i < balls; i++)
            {
                s.Balls.Add(new BallState
                {
                    Id = r.ReadInt32(),
                    Pos = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    Vel = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    SpawnTimer = r.ReadSingle(),
                    LastHitter = r.ReadSByte(),
                });
            }

            int events = r.ReadByte();
            for (int i = 0; i < events; i++)
            {
                var type = (SimEventType)r.ReadByte();
                int side = r.ReadSByte();
                int team = r.ReadSByte();
                var pos = new Vector2(r.ReadSingle(), r.ReadSingle());
                s.Events.Add(new SimEvent(type, side >= 0 ? (GoalSide)side : null, team, pos));
            }

            // Reject anything that would index out of range in the renderer.
            foreach (var p in s.Players)
                if (p.Kind != ControllerKind.Empty && (p.Team < 0 || p.Team >= s.Teams.Length))
                    return null;

            return s;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException)
        {
            return null;
        }
    }
}
