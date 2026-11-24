using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using LastGuardianStanding.Net;
using LastGuardianStanding.Sim;
using NVec = System.Numerics.Vector2;

namespace LastGuardianStanding.Game;

/// <summary>
/// Draws a <see cref="GameState"/>: arena, balls, paddles, HUD and effects. The arena can be rotated so the
/// viewing player's goal is at the bottom; all text stays upright.
/// </summary>
public partial class ArenaView : Node2D
{
    public static readonly Color[] TeamColors =
    {
        new(0.25f, 0.9f, 1f), new(1f, 0.3f, 0.75f), new(0.55f, 1f, 0.3f), new(1f, 0.65f, 0.2f),
    };
    static readonly string[] TeamNames = { "Cyan", "Magenta", "Lime", "Amber" };
    public static readonly string[] SideNames = { "North", "East", "South", "West" };

    const int TrailLength = 14;

    public GameState? State { get; set; }

    /// <summary>Rotation applied to the arena (radians). 0 = North at the top.</summary>
    public float ViewRotation { get; set; }

    /// <summary>Side of the player looking at the screen, or -1 (local play / spectator).</summary>
    public int MySide { get; set; } = -1;

    /// <summary>Label shown next to each side, e.g. "North · W A S D" or a player's name.</summary>
    public string[] SideLabels { get; } = { "", "", "", "" };

    /// <summary>Short name per side, used in "X wins" messages in free-for-all.</summary>
    public string[] DisplayNames { get; } = (string[])SideNames.Clone();

    public string MatchOverHint { get; set; } = "";

    /// <summary>Tall screen: scoreboard goes above the arena, side labels squeeze in.</summary>
    public bool Portrait { get; set; }

    readonly Dictionary<int, List<Vector2>> _trails = new();
    readonly float[] _goalFlash = new float[4];
    float _shake;
    float _time;
    Font _font = null!;
    Vector2[] _arenaPolygon = Array.Empty<Vector2>();

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _arenaPolygon = BuildArenaPolygon();
    }

    public void ResetEffects()
    {
        _trails.Clear();
        Array.Clear(_goalFlash);
        _shake = 0f;
    }

    public void OnEvents(IEnumerable<SimEvent> events)
    {
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SimEventType.Goal when e.Side is GoalSide side:
                    _goalFlash[(int)side] = 0.6f;
                    _shake = MathF.Max(_shake, 14f);
                    break;
                case SimEventType.PlayerEliminated:
                    _shake = MathF.Max(_shake, 22f);
                    break;
                case SimEventType.PaddleHit:
                    _shake = MathF.Max(_shake, 2.5f);
                    break;
            }
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        for (int i = 0; i < 4; i++)
            _goalFlash[i] = MathF.Max(0f, _goalFlash[i] - dt);

        _shake = MathF.Max(0f, _shake - dt * 40f);
        Position = _shake > 0f
            ? new Vector2((float)GD.RandRange(-_shake, _shake), (float)GD.RandRange(-_shake, _shake))
            : Vector2.Zero;

        UpdateTrails();
        QueueRedraw();
    }

    void UpdateTrails()
    {
        if (State == null)
        {
            _trails.Clear();
            return;
        }

        var live = new HashSet<int>();
        foreach (var b in State.Balls)
        {
            if (!b.IsLive) continue;
            live.Add(b.Id);
            if (!_trails.TryGetValue(b.Id, out var list))
                _trails[b.Id] = list = new List<Vector2>();
            list.Add(G(b.Pos));
            if (list.Count > TrailLength) list.RemoveAt(0);
        }
        foreach (var id in _trails.Keys.Where(id => !live.Contains(id)).ToList())
            _trails.Remove(id);
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        // World space, rotated so the viewer's goal is at the bottom.
        DrawSetTransform(Vector2.Zero, ViewRotation, Vector2.One);
        DrawArena();
        if (State != null)
        {
            DrawBalls(State);
            DrawPaddles(State);
        }

        // Screen-aligned HUD.
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        if (State != null)
            DrawHud(State);
    }

    void DrawArena()
    {
        DrawColoredPolygon(_arenaPolygon, new Color(0.055f, 0.06f, 0.1f));

        var faint = new Color(1, 1, 1, 0.05f);
        DrawArc(Vector2.Zero, 90f, 0f, Mathf.Tau, 64, faint, 2f, true);
        DrawLine(new Vector2(-Arena.Half, 0), new Vector2(Arena.Half, 0), faint, 2f);
        DrawLine(new Vector2(0, -Arena.Half), new Vector2(0, Arena.Half), faint, 2f);

        var postColor = Glow(new Color(0.85f, 0.88f, 1f), 1.3f);
        float[] arcStart = { Mathf.Pi / 2f, Mathf.Pi, Mathf.Pi * 1.5f, 0f }; // NW NE SE SW
        for (int k = 0; k < 4; k++)
            DrawArc(G(Arena.Corners[k]), Arena.CornerRadius, arcStart[k], arcStart[k] - Mathf.Pi / 2f, 40, postColor, 5f, true);

        for (int i = 0; i < 4; i++)
        {
            var side = (GoalSide)i;
            var mid = G(Arena.LineCenter(side));
            var tan = G(Arena.Tangent(side));
            var a = mid - tan * Arena.GoalHalfWidth;
            var b = mid + tan * Arena.GoalHalfWidth;

            PlayerState? player = State?.Players[i];
            Color color;
            float width;
            if (player is not { IsActive: true })
            {
                color = player == null ? new Color(1, 1, 1, 0.25f) : new Color(0.7f, 0.72f, 0.8f);
                width = 6f;
            }
            else
            {
                var team = TeamColor(player.Value.Team);
                color = new Color(team, 0.35f + 0.1f * MathF.Sin(_time * 3f));
                width = i == MySide ? 5f : 3f;
            }

            if (_goalFlash[i] > 0f)
            {
                color = Glow(new Color(1, 1, 1), 1f + _goalFlash[i] * 1.5f);
                width = 4f + _goalFlash[i] * 8f;
            }
            DrawLine(a, b, color, width, true);
        }
    }

    void DrawBalls(GameState s)
    {
        foreach (var (_, trail) in _trails)
        {
            for (int i = 0; i < trail.Count; i++)
            {
                float t = (i + 1) / (float)trail.Count;
                DrawCircle(trail[i], Arena.BallRadius * t * 0.9f, new Color(1f, 0.95f, 0.8f, t * 0.35f));
            }
        }

        foreach (var b in s.Balls)
        {
            var pos = G(b.Pos);
            if (!b.IsLive)
            {
                // "Dropping in" telegraph: a ring closing in on the spawn point.
                float t = Mathf.Clamp(b.SpawnTimer / s.SpawnDelay, 0f, 1f);
                DrawArc(pos, Arena.BallRadius + t * 40f, 0f, Mathf.Tau, 32, Glow(new Color(1f, 0.9f, 0.6f), 1.5f) with { A = 1f - t * 0.6f }, 3f, true);
                DrawCircle(pos, Arena.BallRadius * (1f - t), new Color(1f, 0.95f, 0.8f, 0.6f));
                continue;
            }

            var color = b.LastHitter is >= 0 and < 4
                ? TeamColor(s.Players[b.LastHitter].Team).Lerp(Colors.White, 0.55f)
                : new Color(1f, 0.95f, 0.85f);
            DrawCircle(pos, Arena.BallRadius, Glow(color, 2.2f));
        }
    }

    void DrawPaddles(GameState s)
    {
        for (int i = 0; i < 4; i++)
        {
            var p = s.Players[i];
            if (!p.IsActive) continue;
            Arena.PaddleBox((GoalSide)i, p.Lateral, out var c, out var ext);
            var rect = new Rect2(G(c) - G(ext), G(ext) * 2f);
            var color = TeamColor(p.Team);
            DrawRect(rect, Glow(color, 1.8f));
            DrawRect(rect.Grow(-3f), Glow(color.Lerp(Colors.White, 0.5f), 2.2f));
        }
    }

    void DrawHud(GameState s)
    {
        for (int i = 0; i < 4; i++)
        {
            var p = s.Players[i];
            var anchor = ScreenAnchor(i);
            bool narrow = Portrait && MathF.Abs(anchor.X) > 1f; // left/right labels on a tall screen
            float width = narrow ? 180f : 300f;
            int small = narrow ? 15 : 18;
            if (p.Kind == ControllerKind.Empty)
            {
                DrawText(anchor, "— empty —", small, new Color(1, 1, 1, 0.3f), width);
                continue;
            }

            var color = TeamColor(p.Team);
            DrawText(anchor, SideLabels[i], (i == MySide ? 22 : 20) - (narrow ? 4 : 0),
                p.Eliminated ? new Color(1, 1, 1, 0.35f) : color, width);

            if (p.Eliminated)
            {
                DrawText(anchor + new Vector2(0, 26), "KNOCKED OUT", small, new Color(1f, 0.4f, 0.4f, 0.8f), width);
                continue;
            }

            int lives = s.SharedLives ? s.Teams[p.Team].Lives : p.Lives;
            int max = s.SharedLives ? s.Lives * Math.Max(1, s.Teams[p.Team].MemberCount) : s.Lives;
            if (max <= 12)
            {
                float spacing = narrow ? 12f : 16f;
                float x0 = anchor.X - (max - 1) * spacing / 2f;
                for (int l = 0; l < max; l++)
                {
                    var pip = new Vector2(x0 + l * spacing, anchor.Y + 20);
                    if (l < lives) DrawCircle(pip, 5f, Glow(color, 1.5f));
                    else DrawArc(pip, 5f, 0, Mathf.Tau, 16, new Color(color, 0.4f), 1.5f, true);
                }
            }
            else
            {
                DrawText(anchor + new Vector2(0, 26), $"{lives} lives", small, color, width);
            }
        }

        // Scoreboard: beside the arena on wide screens, above it on tall ones.
        var board = Portrait ? new Vector2(-600, -Arena.Half - 250) : new Vector2(-Arena.Half - 420, -Arena.Half + 10);
        DrawString(_font, board, $"ROUND {s.Round}", HorizontalAlignment.Left, -1, 26, Colors.White);
        int row = 1;
        for (int t = 0; t < s.Teams.Length; t++)
        {
            if (s.Teams[t].MemberCount == 0) continue;
            var color = TeamColor(t);
            var pos = board + new Vector2(0, row * 34);
            DrawString(_font, pos, TeamLabel(s, t), HorizontalAlignment.Left, 165, 20, color);
            for (int w = 0; w < s.RoundsToWin; w++)
            {
                var pip = pos + new Vector2(170 + w * 22, -7);
                if (w < s.Teams[t].RoundWins) DrawCircle(pip, 7f, Glow(color, 1.6f));
                else DrawArc(pip, 7f, 0, Mathf.Tau, 16, new Color(color, 0.5f), 2f, true);
            }
            row++;
        }

        var footer = Portrait ? new Vector2(-600, Arena.Half + 130) : new Vector2(-Arena.Half - 420, Arena.Half - 10);
        DrawString(_font, footer, ModeSummary(s), HorizontalAlignment.Left, 380, 16, new Color(1, 1, 1, 0.5f));
        if (s.IsPractice)
            DrawString(_font, footer + new Vector2(0, -26), "PRACTICE — rounds need two teams", HorizontalAlignment.Left, -1, 16, new Color(1f, 0.8f, 0.3f, 0.8f));

        switch (s.Phase)
        {
            case MatchPhase.Countdown:
                DrawBig($"ROUND {s.Round}", -40, 30, Colors.White);
                DrawBig(Mathf.CeilToInt(s.PhaseTimer).ToString(), 30, 72, Glow(Colors.White, 1.4f));
                break;
            case MatchPhase.RoundOver:
                DrawBig(s.RoundWinner >= 0 ? $"{TeamLabel(s, s.RoundWinner)} takes the round" : "Round over", 0, 40,
                    s.RoundWinner >= 0 ? Glow(TeamColor(s.RoundWinner), 1.5f) : Colors.White);
                break;
            case MatchPhase.MatchOver when s.MatchWinner >= 0:
                DrawBig($"{TeamLabel(s, s.MatchWinner)} wins the match!", -20, 46, Glow(TeamColor(s.MatchWinner), 1.6f));
                DrawBig(MatchOverHint, 30, 22, new Color(1, 1, 1, 0.8f));
                break;
        }
    }

    /// <summary>Where a side's label goes on screen, after rotation: just outside its goal.</summary>
    Vector2 ScreenAnchor(int side)
    {
        var outward = -G(Arena.Inward((GoalSide)side)).Rotated(ViewRotation);
        if (outward.Y < -0.5f) return new Vector2(0, -Arena.Half - 62);
        if (outward.Y > 0.5f) return new Vector2(0, Arena.Half + 48);
        float x = Portrait ? Arena.Half + 95 : Arena.Half + 150;
        return outward.X > 0 ? new Vector2(x, -12) : new Vector2(-x, -12);
    }

    string TeamLabel(GameState s, int team)
    {
        // In free-for-all, team id == side index.
        if (s.TeamMode == TeamMode.FreeForAll)
            return DisplayNames[team];
        return $"Team {TeamNames[team % TeamNames.Length]}";
    }

    static string ModeSummary(GameState s)
    {
        string teams = s.TeamMode switch
        {
            TeamMode.TwoVsTwo => "2v2",
            TeamMode.OneOneTwo => "1v1v2",
            TeamMode.OneVsThree => "1v3",
            _ => "Free-for-all",
        };
        string layout = s.TeamMode is TeamMode.TwoVsTwo or TeamMode.OneOneTwo
            ? (s.ResolvedLayout == TeamLayout.Adjacent ? " · side by side" : " · facing") : "";
        string lives = s.SharedLives ? "shared lives" : "personal lives";
        string balls = s.BallMode == BallMode.Chaos ? $"chaos ({s.MaxBalls} balls)" : "single ball";
        return $"{teams}{layout} · {lives} · {balls}";
    }

    void DrawText(Vector2 center, string text, int size, Color color, float width = 300f) =>
        DrawString(_font, center - new Vector2(width / 2f, 0), text, HorizontalAlignment.Center, width, size, color);

    void DrawBig(string text, float y, int size, Color color)
    {
        var pos = new Vector2(-Arena.Half, y);
        DrawString(_font, pos + new Vector2(3, 3), text, HorizontalAlignment.Center, Arena.Half * 2, size, new Color(0, 0, 0, 0.7f));
        DrawString(_font, pos, text, HorizontalAlignment.Center, Arena.Half * 2, size, color);
    }

    // ------------------------------------------------------------------ helpers

    static Color TeamColor(int team) => TeamColors[Math.Abs(team) % TeamColors.Length];

    static Vector2[] BuildArenaPolygon()
    {
        // Walk clockwise: each corner arc runs from a0 to a0 - 90°, joined by the straight goal lines.
        var points = new List<Vector2>();
        (int corner, float a0)[] order =
        {
            (1, Mathf.Pi), (2, Mathf.Pi * 1.5f), (3, 0f), (0, Mathf.Pi / 2f), // NE SE SW NW
        };
        const int segments = 24;
        foreach (var (corner, a0) in order)
        {
            var c = G(Arena.Corners[corner]);
            for (int s = 0; s <= segments; s++)
            {
                float a = a0 - Mathf.Pi / 2f * s / segments;
                points.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Arena.CornerRadius);
            }
        }
        return points.ToArray();
    }

    static Color Glow(Color c, float k) => new(c.R * k, c.G * k, c.B * k, c.A);

    public static Vector2 G(NVec v) => new(v.X, v.Y);
}
