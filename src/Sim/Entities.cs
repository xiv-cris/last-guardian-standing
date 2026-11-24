using System.Collections.Generic;
using System.Numerics;

namespace LastGuardianStanding.Sim;

public sealed class Ball
{
    public int Id;
    public Vector2 Pos;
    public Vector2 Vel;
    public int Corner;              // corner it dropped in from
    public float SpawnTimer;        // > 0 while it is still "dropping" (telegraphed, not yet in play)
    public GoalSide? LastHitter;
    public bool Dead;

    public bool IsLive => SpawnTimer <= 0f && !Dead;
}

public sealed class PlayerSlot
{
    public GoalSide Side;
    public ControllerKind Kind;
    public int Team;
    public int Lives;
    public bool Eliminated;

    // Paddle state. The paddle sits on its goal line; Lateral is its position along Arena.Tangent.
    public float Lateral;
    public float LateralVelocity;

    /// <summary>Desired movement in world space, length &lt;= 1. Set by keyboard, bot or network.</summary>
    public Vector2 Intent;

    /// <summary>Online only: where the remote player's own client shows the paddle. Overrides <see cref="Intent"/>.</summary>
    public float? TargetLateral;

    internal float BotError;
    internal float BotTimer;

    public bool IsActive => Kind != ControllerKind.Empty && !Eliminated;
}

public sealed class Team
{
    public int Id;
    public List<GoalSide> Members { get; } = new();
    public int Lives;               // used when LivesMode is TeamPool
    public bool Eliminated;
    public int RoundWins;
}

public enum MatchPhase { Countdown, Playing, RoundOver, MatchOver }

public enum SimEventType
{
    BallLaunched,
    PaddleHit,
    WallHit,
    Goal,
    PlayerEliminated,
    TeamEliminated,
    RoundWon,
    MatchWon,
}

public readonly record struct SimEvent(SimEventType Type, GoalSide? Side = null, int Team = -1, Vector2 Pos = default);
