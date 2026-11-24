using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LastGuardianStanding.Sim;

/// <summary>
/// The whole game, with no engine dependency. Feed it intents, call <see cref="Step"/> at a fixed rate,
/// then read its state and <see cref="Events"/>. Online, only the host will run this for real.
/// </summary>
public sealed class Simulation
{
    public const float CountdownTime = 2.5f;
    public const float RoundOverTime = 3f;
    public const float SingleSpawnDelay = 1.4f;
    public const float ChaosSpawnDelay = 0.9f;
    public const float ChaosRampInterval = 3f;
    public const int ChaosStartBalls = 3;
    const float AntiStallMinComponent = 0.22f;
    const float WallMinAlong = 0.45f;   // ~27 degrees off the wall normal, at least
    const float RemoteCatchUp = 1.5f;   // remote paddles may chase their reported position at 1.5x paddle speed

    public MatchConfig Config { get; }
    public TeamLayout ResolvedLayout { get; }
    public PlayerSlot[] Players { get; } = new PlayerSlot[4];
    public List<Team> Teams { get; } = new();
    public List<Ball> Balls { get; } = new();

    /// <summary>Things that happened during the last <see cref="Step"/>. Cleared at the start of each step.</summary>
    public List<SimEvent> Events { get; } = new();

    /// <summary>Number of <see cref="Step"/> calls so far.</summary>
    public int Tick { get; private set; }
    public MatchPhase Phase { get; private set; }
    public float PhaseTimer { get; private set; }
    public int Round { get; private set; }
    public int RoundWinner { get; private set; } = -1;
    public int MatchWinner { get; private set; } = -1;

    /// <summary>True when fewer than two teams have players: no rounds are decided.</summary>
    public bool IsPractice { get; }

    public int MaxBalls => Config.BallMode == BallMode.Single ? 1 : Math.Clamp(Config.MaxBalls, 1, 10);

    readonly Random _rng;
    int _nextBallId;
    float _chaosClock;
    int _chaosTarget;

    public Simulation(MatchConfig config)
    {
        Config = config.Clone();
        _rng = Config.Seed is int seed ? new Random(seed) : new Random();

        var teamOf = TeamAssigner.Assign(Config.TeamMode, Config.Layout, _rng, out var resolved);
        ResolvedLayout = resolved;

        for (int t = 0; t <= teamOf.Max(); t++)
            Teams.Add(new Team { Id = t });

        for (int i = 0; i < 4; i++)
        {
            Players[i] = new PlayerSlot { Side = (GoalSide)i, Kind = Config.Slots[i], Team = teamOf[i] };
            if (Config.Slots[i] != ControllerKind.Empty)
                Teams[teamOf[i]].Members.Add((GoalSide)i);
        }

        IsPractice = Teams.Count(t => t.Members.Count > 0) < 2;
        StartRound();
    }

    public void SetIntent(GoalSide side, Vector2 intent)
    {
        if (intent.LengthSquared() > 1f)
            intent = Vector2.Normalize(intent);
        Players[(int)side].Intent = intent;
    }

    /// <summary>
    /// For remote players: the paddle position their own client shows. The paddle chases it (speed-limited),
    /// so what a player sees locally is what the host plays with.
    /// </summary>
    public void SetTargetLateral(GoalSide side, float lateral)
    {
        if (!float.IsFinite(lateral)) return;
        Players[(int)side].TargetLateral = Math.Clamp(lateral, -Arena.PaddleLateralLimit, Arena.PaddleLateralLimit);
    }

    /// <summary>Hands a side over to a bot, e.g. when its player disconnects mid-match.</summary>
    public void ConvertToBot(GoalSide side)
    {
        var p = Players[(int)side];
        if (p.Kind == ControllerKind.Empty) return;
        p.Kind = ControllerKind.Bot;
        p.TargetLateral = null;
    }

    /// <summary>One paddle movement step from an intent. Shared with clients so their own paddle predicts exactly.</summary>
    public static float StepLateral(GoalSide side, float lateral, Vector2 intent, float h)
    {
        float input = Math.Clamp(Vector2.Dot(intent, Arena.Tangent(side)), -1f, 1f);
        return Math.Clamp(lateral + input * Arena.PaddleSpeed * h, -Arena.PaddleLateralLimit, Arena.PaddleLateralLimit);
    }

    public void Step(float dt)
    {
        Tick++;
        Events.Clear();

        foreach (var p in Players)
            if (p.IsActive && p.Kind == ControllerKind.Bot)
                p.Intent = BotBrain.Think(this, p, dt, _rng);

        switch (Phase)
        {
            case MatchPhase.Countdown:
                MovePaddles(dt);
                PhaseTimer -= dt;
                if (PhaseTimer <= 0f)
                    Phase = MatchPhase.Playing;
                break;

            case MatchPhase.Playing:
                UpdateSpawning(dt);
                Integrate(dt);
                break;

            case MatchPhase.RoundOver:
                MovePaddles(dt);
                PhaseTimer -= dt;
                if (PhaseTimer <= 0f)
                    StartRound();
                break;

            case MatchPhase.MatchOver:
                MovePaddles(dt);
                break;
        }
    }

    // ---------------------------------------------------------------- rounds

    void StartRound()
    {
        Round++;
        Balls.Clear();
        RoundWinner = -1;

        foreach (var p in Players)
        {
            p.Eliminated = p.Kind == ControllerKind.Empty;
            p.Lives = Config.Lives;
            p.Lateral = p.LateralVelocity = 0f;
            p.TargetLateral = null;
        }

        foreach (var t in Teams)
        {
            t.Lives = Config.Lives * Math.Max(1, t.Members.Count);
            t.Eliminated = t.Members.Count == 0;
        }

        _chaosClock = 0f;
        _chaosTarget = Math.Min(ChaosStartBalls, MaxBalls);
        Phase = MatchPhase.Countdown;
        PhaseTimer = CountdownTime;
    }

    void OnGoal(PlayerSlot p, Ball b)
    {
        b.Dead = true;
        Events.Add(new SimEvent(SimEventType.Goal, p.Side, p.Team, b.Pos));

        var team = Teams[p.Team];
        bool shared = Config.LivesMode == LivesMode.TeamPool && Config.TeamMode != TeamMode.FreeForAll;

        if (shared)
        {
            team.Lives--;
            if (team.Lives <= 0)
                foreach (var m in team.Members)
                    Eliminate(Players[(int)m]);
        }
        else
        {
            p.Lives--;
            if (p.Lives <= 0)
                Eliminate(p);
        }

        if (!team.Eliminated && team.Members.All(m => Players[(int)m].Eliminated))
        {
            team.Eliminated = true;
            Events.Add(new SimEvent(SimEventType.TeamEliminated, p.Side, team.Id));
        }

        var alive = Teams.Where(t => t.Members.Count > 0 && !t.Eliminated).ToList();
        if (!IsPractice && alive.Count <= 1)
            EndRound(alive.Count == 1 ? alive[0].Id : -1);
        else if (IsPractice && alive.Count == 0)
            EndRound(-1);
    }

    void Eliminate(PlayerSlot p)
    {
        if (p.Eliminated) return;
        p.Eliminated = true;
        Events.Add(new SimEvent(SimEventType.PlayerEliminated, p.Side, p.Team));
    }

    void EndRound(int winner)
    {
        RoundWinner = winner;
        if (winner >= 0)
        {
            var t = Teams[winner];
            t.RoundWins++;
            Events.Add(new SimEvent(SimEventType.RoundWon, Team: winner));
            if (t.RoundWins >= Config.RoundsToWin)
            {
                MatchWinner = winner;
                Phase = MatchPhase.MatchOver;
                Events.Add(new SimEvent(SimEventType.MatchWon, Team: winner));
                return;
            }
        }
        Phase = MatchPhase.RoundOver;
        PhaseTimer = RoundOverTime;
    }

    // ---------------------------------------------------------------- spawning

    void UpdateSpawning(float dt)
    {
        if (Config.BallMode == BallMode.Chaos)
        {
            _chaosClock += dt;
            while (_chaosClock >= ChaosRampInterval && _chaosTarget < MaxBalls)
            {
                _chaosClock -= ChaosRampInterval;
                _chaosTarget++;
            }
        }

        int target = Config.BallMode == BallMode.Single ? 1 : _chaosTarget;
        while (Balls.Count < target)
            SpawnBall();

        foreach (var b in Balls)
        {
            if (b.SpawnTimer <= 0f) continue;
            b.SpawnTimer -= dt;
            if (b.SpawnTimer <= 0f)
                Events.Add(new SimEvent(SimEventType.BallLaunched, Pos: b.Pos));
        }
    }

    void SpawnBall()
    {
        // Prefer a corner that is not already dropping a ball.
        var free = Enumerable.Range(0, 4).Where(c => !Balls.Any(b => b.SpawnTimer > 0f && b.Corner == c)).ToList();
        int corner = free.Count > 0 ? free[_rng.Next(free.Count)] : _rng.Next(4);

        var c = Arena.Corners[corner];
        var toCenter = Vector2.Normalize(-c);
        float spread = (_rng.NextSingle() * 2f - 1f) * 0.5f;

        Balls.Add(new Ball
        {
            Id = _nextBallId++,
            Corner = corner,
            Pos = c + toCenter * (Arena.CornerRadius + Arena.BallRadius + 6f),
            Vel = Rotate(toCenter, spread) * Arena.BallStartSpeed,
            SpawnTimer = Config.BallMode == BallMode.Single ? SingleSpawnDelay : ChaosSpawnDelay,
        });
    }

    // ---------------------------------------------------------------- physics

    void Integrate(float dt)
    {
        float maxSpeed = 0f;
        foreach (var b in Balls)
            if (b.IsLive)
                maxSpeed = MathF.Max(maxSpeed, b.Vel.Length());

        // Sub-step so a fast ball never moves more than half its radius per step (no tunneling).
        int steps = Math.Clamp((int)MathF.Ceiling(maxSpeed * dt / (Arena.BallRadius * 0.5f)), 1, 32);
        float h = dt / steps;

        for (int s = 0; s < steps; s++)
        {
            MovePaddles(h);

            foreach (var b in Balls)
            {
                if (!b.IsLive) continue;
                b.Pos += b.Vel * h;
                CollidePosts(b);
                CollidePaddles(b);
                CollideSides(b);
                if (Phase != MatchPhase.Playing) break;
            }

            if (Phase != MatchPhase.Playing)
            {
                Balls.Clear();
                return;
            }

            CollideBalls();
            Balls.RemoveAll(b => b.Dead);
        }
    }

    void MovePaddles(float h)
    {
        foreach (var p in Players)
        {
            if (!p.IsActive) continue;

            float newLat;
            if (p.TargetLateral is float target)
            {
                float maxStep = Arena.PaddleSpeed * RemoteCatchUp * h;
                newLat = p.Lateral + Math.Clamp(target - p.Lateral, -maxStep, maxStep);
            }
            else
            {
                newLat = StepLateral(p.Side, p.Lateral, p.Intent, h);
            }

            p.LateralVelocity = (newLat - p.Lateral) / h;
            p.Lateral = newLat;
        }
    }

    void CollidePosts(Ball b)
    {
        float minDist = Arena.CornerRadius + Arena.BallRadius;
        foreach (var c in Arena.Corners)
        {
            var d = b.Pos - c;
            float d2 = d.LengthSquared();
            if (d2 >= minDist * minDist) continue;

            float dist = MathF.Sqrt(d2);
            var n = dist > 1e-4f ? d / dist : Vector2.Normalize(-c);
            b.Pos = c + n * minDist;
            if (Vector2.Dot(b.Vel, n) < 0f)
            {
                b.Vel = AntiStall(Vector2.Reflect(b.Vel, n));
                Events.Add(new SimEvent(SimEventType.WallHit, Pos: b.Pos));
            }
        }
    }

    void CollidePaddles(Ball b)
    {
        float r = Arena.BallRadius;
        foreach (var p in Players)
        {
            if (!p.IsActive) continue;

            Arena.PaddleBox(p, out var c, out var ext);
            var d = b.Pos - c;
            var closest = c + Vector2.Clamp(d, -ext, ext);
            var diff = b.Pos - closest;
            float d2 = diff.LengthSquared();
            if (d2 >= r * r) continue;

            var inward = Arena.Inward(p.Side);
            var tangent = Arena.Tangent(p.Side);
            Vector2 n;
            if (d2 > 1e-6f)
            {
                n = diff / MathF.Sqrt(d2);
                b.Pos = closest + n * r;
            }
            else
            {
                // Ball center ended up inside the paddle: pop it out of the front face.
                n = inward;
                b.Pos = c + tangent * Vector2.Dot(d, tangent) + inward * (Arena.PaddleThickness / 2f + r);
            }

            var paddleVel = tangent * p.LateralVelocity;
            var rel = b.Vel - paddleVel;
            if (Vector2.Dot(rel, n) >= 0f) continue;

            if (Vector2.Dot(n, inward) > 0.7f)
            {
                // Front face: classic pong control, the hit position picks the angle.
                float offset = Math.Clamp(Vector2.Dot(b.Pos - c, tangent) / (Arena.PaddleLength / 2f), -1f, 1f);
                float angle = offset * Arena.MaxBounceAngle;
                var dir = inward * MathF.Cos(angle) + tangent * MathF.Sin(angle);
                float speed = b.Vel.Length() * Arena.BallSpeedUpPerHit;
                b.Vel = dir * Math.Clamp(speed, Arena.BallStartSpeed, Arena.BallMaxSpeed);
            }
            else
            {
                b.Vel = EnsureMinSpeed(Vector2.Reflect(rel, n) + paddleVel);
            }

            b.LastHitter = p.Side;
            Events.Add(new SimEvent(SimEventType.PaddleHit, p.Side, p.Team, b.Pos));
        }
    }

    void CollideSides(Ball b)
    {
        float r = Arena.BallRadius;
        for (int i = 0; i < 4; i++)
        {
            var p = Players[i];
            var inward = Arena.Inward(p.Side);
            float depth = Vector2.Dot(b.Pos - Arena.LineCenter(p.Side), inward);

            if (!p.IsActive)
            {
                // Empty or knocked-out side: a solid wall.
                if (depth >= r) continue;
                b.Pos += inward * (r - depth);
                if (Vector2.Dot(b.Vel, inward) < 0f)
                {
                    b.Vel = WallBounce(b.Vel, inward, Arena.Tangent(p.Side));
                    Events.Add(new SimEvent(SimEventType.WallHit, p.Side, Pos: b.Pos));
                }
            }
            else if (depth < -r)
            {
                OnGoal(p, b);
                return;
            }
        }
    }

    void CollideBalls()
    {
        float minDist = Arena.BallRadius * 2f;
        for (int i = 0; i < Balls.Count; i++)
        {
            var a = Balls[i];
            if (!a.IsLive) continue;
            for (int j = i + 1; j < Balls.Count; j++)
            {
                var b = Balls[j];
                if (!b.IsLive) continue;

                var d = b.Pos - a.Pos;
                float d2 = d.LengthSquared();
                if (d2 >= minDist * minDist || d2 < 1e-6f) continue;

                float dist = MathF.Sqrt(d2);
                var n = d / dist;
                float push = (minDist - dist) / 2f;
                a.Pos -= n * push;
                b.Pos += n * push;

                // Equal-mass elastic collision along the contact normal.
                float approach = Vector2.Dot(a.Vel - b.Vel, n);
                if (approach > 0f)
                {
                    a.Vel -= n * approach;
                    b.Vel += n * approach;
                }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Keeps balls from bouncing forever almost parallel to a wall.</summary>
    static Vector2 AntiStall(Vector2 v)
    {
        float speed = v.Length();
        if (speed < 1e-3f) return v;
        var d = v / speed;
        if (MathF.Abs(d.X) < AntiStallMinComponent) d.X = AntiStallMinComponent * (d.X < 0f ? -1f : 1f);
        if (MathF.Abs(d.Y) < AntiStallMinComponent) d.Y = AntiStallMinComponent * (d.Y < 0f ? -1f : 1f);
        return Vector2.Normalize(d) * speed;
    }

    /// <summary>
    /// Bounce off a dead side. The ball always leaves with a solid component along the wall, so it heads for
    /// the live goals instead of ping-ponging between two walls (e.g. 1v1 facing each other).
    /// </summary>
    static Vector2 WallBounce(Vector2 v, Vector2 normal, Vector2 tangent)
    {
        var r = Vector2.Reflect(v, normal);
        float speed = r.Length();
        if (speed < 1e-3f) return r;
        float along = Vector2.Dot(r, tangent) / speed;
        if (MathF.Abs(along) >= WallMinAlong) return r;

        float sign = along < 0f ? -1f : 1f;
        float off = MathF.Sqrt(1f - WallMinAlong * WallMinAlong);
        return (tangent * (sign * WallMinAlong) + normal * off) * speed;
    }

    static Vector2 EnsureMinSpeed(Vector2 v)
    {
        float speed = v.Length();
        float min = Arena.BallStartSpeed * 0.8f;
        if (speed >= min) return speed > Arena.BallMaxSpeed ? v / speed * Arena.BallMaxSpeed : v;
        return speed < 1e-3f ? new Vector2(0, min) : v / speed * min;
    }

    static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
