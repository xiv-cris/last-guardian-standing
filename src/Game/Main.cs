using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using LastGuardianStanding.Sim;
using NVec = System.Numerics.Vector2;

namespace LastGuardianStanding.Game;

/// <summary>
/// Local prototype front-end: owns a <see cref="Simulation"/>, feeds it keyboard input, and draws it.
/// </summary>
public partial class Main : Node2D
{
    static readonly Color[] TeamColors =
    {
        new(0.25f, 0.9f, 1f), new(1f, 0.3f, 0.75f), new(0.55f, 1f, 0.3f), new(1f, 0.65f, 0.2f),
    };
    static readonly string[] TeamNames = { "Cyan", "Magenta", "Lime", "Amber" };
    static readonly string[] SideNames = { "North", "East", "South", "West" };
    static readonly string[] KeyHints = { "W A S D", "Numpad 8 4 5 6", "Arrow keys", "I J K L" };

    // up, left, down, right — screen directions, which are also world directions.
    static readonly Key[][] SideKeys =
    {
        new[] { Key.W, Key.A, Key.S, Key.D },
        new[] { Key.Kp8, Key.Kp4, Key.Kp5, Key.Kp6 },
        new[] { Key.Up, Key.Left, Key.Down, Key.Right },
        new[] { Key.I, Key.J, Key.K, Key.L },
    };

    const int TrailLength = 14;

    Simulation? _sim;
    readonly MatchConfig _config = new();
    Camera2D _camera = null!;
    Vector2[] _arenaPolygon = Array.Empty<Vector2>();
    readonly Dictionary<int, List<Vector2>> _trails = new();
    readonly float[] _goalFlash = new float[4];
    float _shake;
    float _time;
    Font _font = null!;

    // Menu
    CanvasLayer _menuLayer = null!;
    OptionButton _teamMode = null!, _layout = null!, _livesMode = null!, _ballMode = null!;
    SpinBox _lives = null!, _rounds = null!, _maxBalls = null!;
    readonly OptionButton[] _slots = new OptionButton[4];

    public override void _Ready()
    {
        _camera = GetNode<Camera2D>("Camera2D");
        _font = ThemeDB.FallbackFont;
        _arenaPolygon = BuildArenaPolygon();
        BuildMenu();

        // `godot -- --demo` starts an all-bot chaos match straight away (handy for testing).
        if (OS.GetCmdlineUserArgs().Contains("--demo"))
        {
            for (int i = 0; i < 4; i++) _config.Slots[i] = ControllerKind.Bot;
            _config.BallMode = BallMode.Chaos;
            _config.TeamMode = TeamMode.TwoVsTwo;
            StartMatch(fromMenu: false);
        }
    }

    // ------------------------------------------------------------------ loop

    public override void _PhysicsProcess(double delta)
    {
        if (_sim == null) return;

        for (int i = 0; i < 4; i++)
            if (_sim.Players[i].Kind == ControllerKind.Human)
                _sim.SetIntent((GoalSide)i, ReadKeys(i));

        _sim.Step((float)delta);
        HandleEvents(_sim.Events);
        UpdateTrails();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        for (int i = 0; i < 4; i++)
            _goalFlash[i] = MathF.Max(0f, _goalFlash[i] - dt);

        _shake = MathF.Max(0f, _shake - dt * 40f);
        _camera.Offset = _shake > 0f
            ? new Vector2((float)GD.RandRange(-_shake, _shake), (float)GD.RandRange(-_shake, _shake))
            : Vector2.Zero;

        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (key.Keycode == Key.Escape && _sim != null)
        {
            ShowMenu();
        }
        else if (_sim?.Phase == MatchPhase.MatchOver)
        {
            if (key.Keycode == Key.Space) ShowMenu();
            else if (key.Keycode == Key.R) StartMatch(fromMenu: false);
        }
    }

    static NVec ReadKeys(int side)
    {
        var k = SideKeys[side];
        float x = (Input.IsPhysicalKeyPressed(k[3]) ? 1 : 0) - (Input.IsPhysicalKeyPressed(k[1]) ? 1 : 0);
        float y = (Input.IsPhysicalKeyPressed(k[2]) ? 1 : 0) - (Input.IsPhysicalKeyPressed(k[0]) ? 1 : 0);
        return new NVec(x, y);
    }

    void HandleEvents(List<SimEvent> events)
    {
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SimEventType.Goal:
                    _goalFlash[(int)e.Side!.Value] = 0.6f;
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

    void UpdateTrails()
    {
        var live = new HashSet<int>();
        foreach (var b in _sim!.Balls)
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

    // ------------------------------------------------------------------ match / menu

    void StartMatch(bool fromMenu)
    {
        if (fromMenu)
        {
            _config.TeamMode = (TeamMode)_teamMode.Selected;
            _config.Layout = (TeamLayout)_layout.Selected;
            _config.LivesMode = (LivesMode)_livesMode.Selected;
            _config.BallMode = (BallMode)_ballMode.Selected;
            _config.Lives = (int)_lives.Value;
            _config.RoundsToWin = (int)_rounds.Value;
            _config.MaxBalls = (int)_maxBalls.Value;
            for (int i = 0; i < 4; i++)
                _config.Slots[i] = (ControllerKind)_slots[i].Selected;
        }

        _sim = new Simulation(_config);
        _trails.Clear();
        _menuLayer.Visible = false;
    }

    void ShowMenu()
    {
        _sim = null;
        _trails.Clear();
        _menuLayer.Visible = true;
    }

    void BuildMenu()
    {
        _menuLayer = new CanvasLayer();
        AddChild(_menuLayer);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _menuLayer.AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _menuLayer.AddChild(center);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 28);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        var title = new Label { Text = "LAST GUARDIAN STANDING", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 44);
        title.AddThemeColorOverride("font_color", TeamColors[0]);
        vbox.AddChild(title);
        vbox.AddChild(new Label { Text = "Four-way paddle ball — local prototype", HorizontalAlignment = HorizontalAlignment.Center });
        vbox.AddChild(new HSeparator());

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 8);
        vbox.AddChild(grid);

        _teamMode = AddOption(grid, "Teams", (int)_config.TeamMode, "Free-for-all (1v1v1v1)", "2 v 2", "1 v 1 v 2", "1 v 3");
        _layout = AddOption(grid, "Team layout", (int)_config.Layout, "Random", "Side by side", "Facing each other");
        _livesMode = AddOption(grid, "Lives", (int)_config.LivesMode, "Personal", "Shared per team");
        _ballMode = AddOption(grid, "Balls", (int)_config.BallMode, "Single", "Chaos");
        _lives = AddSpin(grid, "Lives each", 1, 20, _config.Lives);
        _rounds = AddSpin(grid, "Rounds to win", 1, 9, _config.RoundsToWin);
        _maxBalls = AddSpin(grid, "Max balls (chaos)", 2, 10, _config.MaxBalls);

        vbox.AddChild(new HSeparator());
        var players = new GridContainer { Columns = 2 };
        players.AddThemeConstantOverride("h_separation", 24);
        players.AddThemeConstantOverride("v_separation", 8);
        vbox.AddChild(players);
        for (int i = 0; i < 4; i++)
            _slots[i] = AddOption(players, $"{SideNames[i]}   ({KeyHints[i]})", (int)_config.Slots[i], "Empty", "Human", "Bot");

        vbox.AddChild(new HSeparator());
        var start = new Button { Text = "Start match", CustomMinimumSize = new Vector2(0, 44) };
        start.Pressed += () => StartMatch(fromMenu: true);
        vbox.AddChild(start);
        vbox.AddChild(new Label
        {
            Text = "Esc: back to menu.",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.6f),
        });
    }

    static OptionButton AddOption(GridContainer grid, string label, int selected, params string[] items)
    {
        grid.AddChild(new Label { Text = label });
        var ob = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        foreach (var item in items) ob.AddItem(item);
        ob.Selected = selected;
        grid.AddChild(ob);
        return ob;
    }

    static SpinBox AddSpin(GridContainer grid, string label, int min, int max, int value)
    {
        grid.AddChild(new Label { Text = label });
        var sb = new SpinBox { MinValue = min, MaxValue = max, Value = value, Step = 1 };
        grid.AddChild(sb);
        return sb;
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        DrawArena();
        if (_sim == null) return;

        DrawBalls();
        DrawPaddles();
        DrawHud();
    }

    void DrawArena()
    {
        DrawColoredPolygon(_arenaPolygon, new Color(0.055f, 0.06f, 0.1f));

        var faint = new Color(1, 1, 1, 0.05f);
        DrawArc(Vector2.Zero, 90f, 0f, Mathf.Tau, 64, faint, 2f, true);
        DrawLine(new Vector2(-Arena.Half, 0), new Vector2(Arena.Half, 0), faint, 2f);
        DrawLine(new Vector2(0, -Arena.Half), new Vector2(0, Arena.Half), faint, 2f);

        // Corner posts.
        var postColor = Glow(new Color(0.85f, 0.88f, 1f), 1.3f);
        float[] arcStart = { Mathf.Pi / 2f, Mathf.Pi, Mathf.Pi * 1.5f, 0f }; // NW NE SE SW
        for (int k = 0; k < 4; k++)
            DrawArc(G(Arena.Corners[k]), Arena.CornerRadius, arcStart[k], arcStart[k] - Mathf.Pi / 2f, 40, postColor, 5f, true);

        // Goal lines / walls.
        for (int i = 0; i < 4; i++)
        {
            var side = (GoalSide)i;
            var mid = G(Arena.LineCenter(side));
            var tan = G(Arena.Tangent(side));
            var a = mid - tan * Arena.GoalHalfWidth;
            var b = mid + tan * Arena.GoalHalfWidth;

            var player = _sim?.Players[i];
            bool isWall = player == null || !player.IsActive;
            Color color;
            float width;
            if (isWall)
            {
                color = player == null ? new Color(1, 1, 1, 0.25f) : new Color(0.7f, 0.72f, 0.8f);
                width = 6f;
            }
            else
            {
                var team = TeamColors[player!.Team % TeamColors.Length];
                color = new Color(team, 0.35f + 0.1f * MathF.Sin(_time * 3f));
                width = 3f;
            }

            if (_goalFlash[i] > 0f)
            {
                color = Glow(new Color(1, 1, 1), 1f + _goalFlash[i] * 1.5f);
                width = 4f + _goalFlash[i] * 8f;
            }
            DrawLine(a, b, color, width, true);
        }
    }

    void DrawBalls()
    {
        foreach (var (_, trail) in _trails)
        {
            for (int i = 0; i < trail.Count; i++)
            {
                float t = (i + 1) / (float)trail.Count;
                DrawCircle(trail[i], Arena.BallRadius * t * 0.9f, new Color(1f, 0.95f, 0.8f, t * 0.35f));
            }
        }

        foreach (var b in _sim!.Balls)
        {
            var pos = G(b.Pos);
            if (!b.IsLive)
            {
                // "Dropping in" telegraph: a ring closing in on the spawn point.
                float total = _sim.Config.BallMode == BallMode.Single ? Simulation.SingleSpawnDelay : Simulation.ChaosSpawnDelay;
                float t = Mathf.Clamp(b.SpawnTimer / total, 0f, 1f);
                DrawArc(pos, Arena.BallRadius + t * 40f, 0f, Mathf.Tau, 32, Glow(new Color(1f, 0.9f, 0.6f), 1.5f) with { A = 1f - t * 0.6f }, 3f, true);
                DrawCircle(pos, Arena.BallRadius * (1f - t) , new Color(1f, 0.95f, 0.8f, 0.6f));
                continue;
            }

            var color = b.LastHitter is GoalSide s
                ? TeamColors[_sim.Players[(int)s].Team % TeamColors.Length].Lerp(Colors.White, 0.55f)
                : new Color(1f, 0.95f, 0.85f);
            DrawCircle(pos, Arena.BallRadius, Glow(color, 2.2f));
        }
    }

    void DrawPaddles()
    {
        foreach (var p in _sim!.Players)
        {
            if (!p.IsActive) continue;
            Arena.PaddleBox(p, out var c, out var ext);
            var rect = new Rect2(G(c) - G(ext), G(ext) * 2f);
            var color = TeamColors[p.Team % TeamColors.Length];
            DrawRect(rect, Glow(color, 1.8f));
            DrawRect(rect.Grow(-3f), Glow(color.Lerp(Colors.White, 0.5f), 2.2f));
        }
    }

    void DrawHud()
    {
        var sim = _sim!;
        bool sharedLives = sim.Config.LivesMode == LivesMode.TeamPool && sim.Config.TeamMode != TeamMode.FreeForAll;

        // Per-side labels outside the arena.
        Vector2[] anchors =
        {
            new(0, -Arena.Half - 62), new(Arena.Half + 150, -12), new(0, Arena.Half + 48), new(-Arena.Half - 150, -12),
        };
        for (int i = 0; i < 4; i++)
        {
            var p = sim.Players[i];
            var anchor = anchors[i];
            if (p.Kind == ControllerKind.Empty)
            {
                DrawText(anchor, "— empty —", 18, new Color(1, 1, 1, 0.3f));
                continue;
            }

            var color = TeamColors[p.Team % TeamColors.Length];
            string who = p.Kind == ControllerKind.Human ? $"{SideNames[i]} · {KeyHints[i]}" : $"{SideNames[i]} · BOT";
            DrawText(anchor, who, 20, p.Eliminated ? new Color(1, 1, 1, 0.35f) : color);

            if (p.Eliminated)
            {
                DrawText(anchor + new Vector2(0, 26), "KNOCKED OUT", 18, new Color(1f, 0.4f, 0.4f, 0.8f));
                continue;
            }

            int lives = sharedLives ? sim.Teams[p.Team].Lives : p.Lives;
            int max = sharedLives ? sim.Config.Lives * Math.Max(1, sim.Teams[p.Team].Members.Count) : sim.Config.Lives;
            if (max <= 12)
            {
                float spacing = 16f;
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
                DrawText(anchor + new Vector2(0, 26), $"{lives} lives", 18, color);
            }
        }

        // Scoreboard.
        var board = new Vector2(-Arena.Half - 420, -Arena.Half + 10);
        DrawString(_font, board, $"ROUND {sim.Round}", HorizontalAlignment.Left, -1, 26, Colors.White);
        int row = 1;
        foreach (var t in sim.Teams.Where(t => t.Members.Count > 0))
        {
            var color = TeamColors[t.Id % TeamColors.Length];
            var pos = board + new Vector2(0, row * 34);
            DrawString(_font, pos, TeamLabel(t.Id), HorizontalAlignment.Left, -1, 20, color);
            for (int w = 0; w < sim.Config.RoundsToWin; w++)
            {
                var pip = pos + new Vector2(170 + w * 22, -7);
                if (w < t.RoundWins) DrawCircle(pip, 7f, Glow(color, 1.6f));
                else DrawArc(pip, 7f, 0, Mathf.Tau, 16, new Color(color, 0.5f), 2f, true);
            }
            row++;
        }

        var footer = new Vector2(-Arena.Half - 420, Arena.Half - 10);
        DrawString(_font, footer, ModeSummary(sim), HorizontalAlignment.Left, 380, 16, new Color(1, 1, 1, 0.5f));
        if (sim.IsPractice)
            DrawString(_font, footer + new Vector2(0, -26), "PRACTICE — rounds need two teams", HorizontalAlignment.Left, -1, 16, new Color(1f, 0.8f, 0.3f, 0.8f));

        // Center message.
        switch (sim.Phase)
        {
            case MatchPhase.Countdown:
                DrawBig($"ROUND {sim.Round}", -40, 30, Colors.White);
                DrawBig(Mathf.CeilToInt(sim.PhaseTimer).ToString(), 30, 72, Glow(Colors.White, 1.4f));
                break;
            case MatchPhase.RoundOver:
                DrawBig(sim.RoundWinner >= 0 ? $"{TeamLabel(sim.RoundWinner)} takes the round" : "Round over", 0, 40,
                    sim.RoundWinner >= 0 ? Glow(TeamColors[sim.RoundWinner % TeamColors.Length], 1.5f) : Colors.White);
                break;
            case MatchPhase.MatchOver:
                DrawBig($"{TeamLabel(sim.MatchWinner)} wins the match!", -20, 46, Glow(TeamColors[sim.MatchWinner % TeamColors.Length], 1.6f));
                DrawBig("Space: menu     R: rematch", 30, 22, new Color(1, 1, 1, 0.8f));
                break;
        }
    }

    string TeamLabel(int team)
    {
        // In free-for-all, team id == side index.
        if (_sim!.Config.TeamMode == TeamMode.FreeForAll)
            return SideNames[team];
        return $"Team {TeamNames[team % TeamNames.Length]}";
    }

    static string ModeSummary(Simulation sim)
    {
        string teams = sim.Config.TeamMode switch
        {
            TeamMode.TwoVsTwo => "2v2",
            TeamMode.OneOneTwo => "1v1v2",
            TeamMode.OneVsThree => "1v3",
            _ => "Free-for-all",
        };
        string layout = sim.Config.TeamMode is TeamMode.TwoVsTwo or TeamMode.OneOneTwo
            ? (sim.ResolvedLayout == TeamLayout.Adjacent ? " · side by side" : " · facing") : "";
        string lives = sim.Config.LivesMode == LivesMode.TeamPool && sim.Config.TeamMode != TeamMode.FreeForAll ? "shared lives" : "personal lives";
        string balls = sim.Config.BallMode == BallMode.Chaos ? $"chaos ({sim.MaxBalls} balls)" : "single ball";
        return $"{teams}{layout} · {lives} · {balls}";
    }

    void DrawText(Vector2 center, string text, int size, Color color) =>
        DrawString(_font, center - new Vector2(150, 0), text, HorizontalAlignment.Center, 300, size, color);

    void DrawBig(string text, float y, int size, Color color)
    {
        var pos = new Vector2(-Arena.Half, y);
        DrawString(_font, pos + new Vector2(3, 3), text, HorizontalAlignment.Center, Arena.Half * 2, size, new Color(0, 0, 0, 0.7f));
        DrawString(_font, pos, text, HorizontalAlignment.Center, Arena.Half * 2, size, color);
    }

    // ------------------------------------------------------------------ helpers

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

    static Vector2 G(NVec v) => new(v.X, v.Y);
}
