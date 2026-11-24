using System;
using System.Numerics;

namespace Quadra.Sim;

/// <summary>Simple paddle AI: guard against the ball that will reach our line soonest, with some human-ish error.</summary>
public static class BotBrain
{
    /// <summary>Bots move at most this fraction of full paddle speed.</summary>
    const float MaxEffort = 0.8f;

    public static Vector2 Think(Simulation sim, PlayerSlot me, float dt, Random rng)
    {
        me.BotTimer -= dt;
        if (me.BotTimer <= 0f)
        {
            me.BotTimer = 0.35f + rng.NextSingle() * 0.4f;
            me.BotError = (rng.NextSingle() * 2f - 1f) * Arena.PaddleLength * 0.4f;
        }

        var inward = Arena.Inward(me.Side);
        var tangent = Arena.Tangent(me.Side);
        var line = Arena.LineCenter(me.Side);

        float bestTime = float.MaxValue;
        float target = 0f;
        float threatSpeed = 0f;

        foreach (var b in sim.Balls)
        {
            if (!b.IsLive) continue;
            float approach = -Vector2.Dot(b.Vel, inward);
            if (approach < 30f) continue;
            float depth = Vector2.Dot(b.Pos - line, inward);
            if (depth < -Arena.BallRadius) continue;

            float t = depth / approach;
            if (t >= bestTime) continue;
            bestTime = t;
            threatSpeed = b.Vel.Length();
            target = Vector2.Dot(b.Pos - line, tangent) + Vector2.Dot(b.Vel, tangent) * t;
        }

        if (bestTime < float.MaxValue)
            target += me.BotError * threatSpeed / Arena.BallStartSpeed; // faster balls -> sloppier reads

        target = Math.Clamp(target, -Arena.PaddleLateralLimit, Arena.PaddleLateralLimit);
        float move = Math.Clamp((target - me.Lateral) / 30f, -MaxEffort, MaxEffort);
        return tangent * move;
    }
}
