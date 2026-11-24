using System;
using Godot;

namespace LastGuardianStanding.Game;

/// <summary>
/// On-screen buttons during a match, so touch devices (no Esc / Space / R) can leave, return to the
/// lobby and start a rematch. Shown on desktop too.
/// </summary>
public partial class MatchOverlay : CanvasLayer
{
    public event Action? ExitPressed;
    public event Action? RematchPressed;
    public event Action? MenuPressed;

    Button _exit = null!;
    Button _rematch = null!;
    Button _menu = null!;
    HBoxContainer _endButtons = null!;
    bool _hostControls;

    public override void _Ready()
    {
        _exit = MakeButton("Exit", new Vector2(120, 56));
        _exit.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 16);
        _exit.GrowHorizontal = Control.GrowDirection.Begin; // longer labels grow leftward, not off-screen
        _exit.Pressed += () => ExitPressed?.Invoke();
        AddChild(_exit);

        _endButtons = new HBoxContainer();
        _endButtons.AddThemeConstantOverride("separation", 24);
        _rematch = MakeButton("Rematch", new Vector2(220, 64));
        _rematch.Pressed += () => RematchPressed?.Invoke();
        _menu = MakeButton("Menu", new Vector2(220, 64));
        _menu.Pressed += () => MenuPressed?.Invoke();
        _endButtons.AddChild(_menu);
        _endButtons.AddChild(_rematch);
        AddChild(_endButtons);
        _endButtons.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 48);
        _endButtons.GrowHorizontal = Control.GrowDirection.Both;
        _endButtons.GrowVertical = Control.GrowDirection.Begin;
        _endButtons.Visible = false;
    }

    /// <param name="exitText">Label of the corner button ("Menu", "Lobby", "Leave").</param>
    /// <param name="hostControls">Whether this player may start a rematch / end the match for everyone.</param>
    public void Configure(string exitText, string menuText, bool hostControls)
    {
        _exit.Text = exitText;
        _menu.Text = menuText;
        _hostControls = hostControls;
    }

    public void SetMatchOver(bool over) => _endButtons.Visible = over && _hostControls;

    static Button MakeButton(string text, Vector2 minSize)
    {
        var b = new Button { Text = text, CustomMinimumSize = minSize, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", 24);
        return b;
    }
}
