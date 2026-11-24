using System.Numerics;

namespace LastGuardianStanding.Sim;

/// <summary>The four goal sides, clockwise from the top. World space is y-down (same as Godot).</summary>
public enum GoalSide { North = 0, East = 1, South = 2, West = 3 }

/// <summary>Fixed arena geometry and tuning constants. The arena is a square centered on the origin.</summary>
public static class Arena
{
    public const float Half = 450f;                 // half the side length of the square
    public const float CornerRadius = 110f;         // radius of the quarter-circle posts
    public const float GoalHalfWidth = Half - CornerRadius;

    public const float PaddleLength = 130f;
    public const float PaddleThickness = 16f;
    public const float PaddleSpeed = 640f;
    public const float PaddleLateralLimit = GoalHalfWidth - PaddleLength / 2f;

    public const float BallRadius = 10f;
    public const float BallStartSpeed = 420f;
    public const float BallMaxSpeed = 1100f;
    public const float BallSpeedUpPerHit = 1.05f;
    public const float MaxBounceAngle = 1.05f;      // ~60 degrees off the paddle normal at the paddle tips

    /// <summary>Corner post centers: NW, NE, SE, SW.</summary>
    public static readonly Vector2[] Corners =
    {
        new(-Half, -Half), new(Half, -Half), new(Half, Half), new(-Half, Half),
    };

    /// <summary>Unit vector pointing from the side's goal line into the arena.</summary>
    public static Vector2 Inward(GoalSide side) => side switch
    {
        GoalSide.North => new Vector2(0, 1),
        GoalSide.East => new Vector2(-1, 0),
        GoalSide.South => new Vector2(0, -1),
        _ => new Vector2(1, 0),
    };

    /// <summary>Unit vector along the goal line (+x for North/South, +y for East/West).</summary>
    public static Vector2 Tangent(GoalSide side) =>
        side is GoalSide.North or GoalSide.South ? Vector2.UnitX : Vector2.UnitY;

    public static Vector2 LineCenter(GoalSide side) => -Inward(side) * Half;

    /// <summary>Axis-aligned box of a paddle: center and half extents.</summary>
    public static void PaddleBox(PlayerSlot p, out Vector2 center, out Vector2 halfExtents)
    {
        center = LineCenter(p.Side)
                 + Inward(p.Side) * (PaddleThickness / 2f)
                 + Tangent(p.Side) * p.Lateral;
        halfExtents = p.Side is GoalSide.North or GoalSide.South
            ? new Vector2(PaddleLength / 2f, PaddleThickness / 2f)
            : new Vector2(PaddleThickness / 2f, PaddleLength / 2f);
    }
}
