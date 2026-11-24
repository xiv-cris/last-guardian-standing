using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using LastGuardianStanding.Net;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Game;

/// <summary>
/// All menu screens, built in code: the main screen (name, local / host / join), one setup panel that
/// serves as both the local match setup and the online lobby, and the controls screen (key bindings,
/// gamepads). Raises events; holds no game logic.
/// </summary>
public partial class MenuUI : CanvasLayer
{
    public event Action? LocalPressed;
    public event Action? HostPressed;
    public event Action? JoinPressed;
    public event Action? StartPressed;
    public event Action? BackPressed;
    public event Action? SettingsEdited;
    public event Action<int, int>? SlotEdited; // side, selected index
    public event Action<bool>? MusicToggled;

    Control _mainPanel = null!;
    Control _setupPanel = null!;
    Control _controlsPanel = null!;

    LineEdit _name = null!;
    LineEdit _address = null!;
    SpinBox _port = null!;
    Label _mainStatus = null!;
    Button _localBtn = null!, _hostBtn = null!, _joinBtn = null!, _controlsBtn = null!;

    // Controls screen
    readonly List<(Button button, string id, int slot)> _bindButtons = new();
    readonly OptionButton[] _padPickers = new OptionButton[4];
    (string id, int slot)? _listening;
    bool _usingPad;
    Label _controlsStatus = null!;
    Label _padList = null!;
    CheckButton _music = null!;

    Label _setupTitle = null!;
    Label _players = null!;
    Label _setupStatus = null!;
    OptionButton _teamMode = null!, _layout = null!, _livesMode = null!, _ballMode = null!;
    SpinBox _lives = null!, _rounds = null!, _maxBalls = null!;
    readonly OptionButton[] _slots = new OptionButton[4];
    readonly Label[] _slotLabels = new Label[4];
    Button _start = null!, _back = null!;
    bool _refreshing;

    public bool IsOnMainScreen => Visible && _mainPanel.Visible;
    public bool IsOnControlsScreen => Visible && _controlsPanel.Visible;
    public string PlayerName => LobbyState.CleanName(_name.Text);
    public string Address => _address.Text.Trim();
    public int Port => (int)_port.Value;

    public void SetPlayerName(string name) => _name.Text = name;
    public void SetAddress(string address) => _address.Text = address;
    public void SetPort(int port) => _port.Value = port;
    public void SetMusic(bool on) => _music.SetPressedNoSignal(on);

    public override void _Ready()
    {
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);

        _mainPanel = BuildMainPanel();
        _setupPanel = BuildSetupPanel();
        _controlsPanel = BuildControlsPanel();
        Input.JoyConnectionChanged += OnJoyConnectionChanged;
    }

    public override void _ExitTree() => Input.JoyConnectionChanged -= OnJoyConnectionChanged;

    void OnJoyConnectionChanged(long device, bool connected) => RefreshControls();

    public override void _Input(InputEvent @event)
    {
        bool padUsed = @event is InputEventJoypadButton { Pressed: true }
            || @event is InputEventJoypadMotion m && Math.Abs(m.AxisValue) > 0.5f;
        if (padUsed) _usingPad = true;
        else if (@event is InputEventMouseButton { Pressed: true }) _usingPad = false;

        if (!Visible) return;

        // Waiting for a key on the controls screen: the next key press is the new binding (any key, Esc too).
        if (_listening is { } target && @event is InputEventKey { Pressed: true, Echo: false } key)
        {
            var removed = Controls.Assign(target.id, target.slot, Controls.BindableKey(key));
            _listening = null;
            _controlsStatus.Text = removed.Count > 0
                ? $"{Controls.KeyName(Controls.BindableKey(key))} was also removed from: {string.Join(", ", removed)}."
                : DefaultControlsHint;
            RefreshControls();
            GetViewport().SetInputAsHandled();
            return;
        }

        // A gamepad touched while nothing has focus: focus the first button so the d-pad / stick can navigate.
        // That first touch only wakes the cursor up (it doesn't also press the button); "back" still works.
        if (padUsed && GetViewport().GuiGetFocusOwner() == null)
        {
            FirstFocusable(VisiblePanel())?.GrabFocus();
            if (!@event.IsAction("ui_cancel")) GetViewport().SetInputAsHandled();
            return;
        }

        // Gamepad left/right changes the focused number box or dropdown in place (no typing, no popup).
        if (@event is InputEventJoypadButton or InputEventJoypadMotion)
        {
            int delta = @event.IsActionPressed("ui_left") ? -1 : @event.IsActionPressed("ui_right") ? 1 : 0;
            if (delta != 0 && StepFocused(GetViewport().GuiGetFocusOwner(), delta))
                GetViewport().SetInputAsHandled();
        }
    }

    static bool StepFocused(Control? focus, int delta)
    {
        switch (focus)
        {
            case LineEdit { } edit when edit.GetParent() is SpinBox { Editable: true } spin:
                spin.Value += delta * spin.Step;
                return true;
            case OptionButton { Disabled: false } option when option.ItemCount > 0:
                int index = Math.Clamp(option.Selected + delta, 0, option.ItemCount - 1);
                if (index != option.Selected)
                {
                    option.Select(index);
                    option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
                }
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ public screens

    public void ShowMain(string status = "", bool busy = false)
    {
        ShowPanel(_mainPanel);
        _mainStatus.Text = status;
        _localBtn.Disabled = _hostBtn.Disabled = _joinBtn.Disabled = _controlsBtn.Disabled = busy;
    }

    public void ShowControls()
    {
        ShowPanel(_controlsPanel);
        _controlsStatus.Text = DefaultControlsHint;
        RefreshControls();
    }

    void ShowPanel(Control panel)
    {
        Visible = true;
        _listening = null;
        _mainPanel.Visible = panel == _mainPanel;
        _setupPanel.Visible = panel == _setupPanel;
        _controlsPanel.Visible = panel == _controlsPanel;
        GetViewport().GuiReleaseFocus();
        // Playing with a gamepad: put the cursor on the new screen straight away.
        if (_usingPad) Callable.From(() => FirstFocusable(panel)?.GrabFocus()).CallDeferred();
    }

    Control VisiblePanel() => _controlsPanel.Visible ? _controlsPanel : _setupPanel.Visible ? _setupPanel : _mainPanel;

    static Control? FirstFocusable(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is BaseButton { Disabled: false, FocusMode: Control.FocusModeEnum.All, Visible: true } b) return b;
            if (child is Control { Visible: false }) continue;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }

    public void ShowLocalSetup(MatchConfig cfg)
    {
        _refreshing = true;
        ShowPanel(_setupPanel);

        _setupTitle.Text = "Local match";
        _players.Visible = false;
        WriteSettings(cfg.TeamMode, cfg.Layout, cfg.LivesMode, cfg.BallMode, cfg.Lives, cfg.RoundsToWin, cfg.MaxBalls);
        SetSettingsEditable(true);

        for (int i = 0; i < 4; i++)
        {
            _slotLabels[i].Text = $"{ArenaView.SideNames[i]}   ({LocalControlsHint(i)})";
            _slots[i].Clear();
            foreach (var item in new[] { "Empty", "Human", "Bot" }) _slots[i].AddItem(item);
            _slots[i].Selected = (int)cfg.Slots[i];
            _slots[i].Disabled = false;
        }

        _start.Visible = true;
        _start.Text = "Start match";
        _back.Text = "Back";
        _setupStatus.Text = "Esc during a match: back to this screen.";
        _refreshing = false;
    }

    /// <summary>Shows or refreshes the lobby. Clients see it read-only.</summary>
    public void ShowLobby(LobbyState lobby, bool isHost, long myId, string status)
    {
        _refreshing = true;
        bool wasShowing = _setupPanel.Visible && Visible;
        if (!wasShowing) ShowPanel(_setupPanel);

        _setupTitle.Text = isHost ? "Online lobby — you are hosting" : "Online lobby";
        _players.Visible = true;
        _players.Text = "Players: " + string.Join(",  ", lobby.Players.Select(p =>
            p.Name + (p.PeerId == 1 ? " (host)" : "") + (p.PeerId == myId ? " (you)" : "")));

        WriteSettings(lobby.TeamMode, lobby.Layout, lobby.LivesMode, lobby.BallMode, lobby.Lives, lobby.RoundsToWin, lobby.MaxBalls);
        SetSettingsEditable(isHost);

        for (int i = 0; i < 4; i++)
        {
            _slotLabels[i].Text = ArenaView.SideNames[i];
            _slots[i].Clear();
            _slots[i].AddItem("Empty");
            _slots[i].AddItem("Bot");
            foreach (var p in lobby.Players) _slots[i].AddItem(p.Name);

            long owner = lobby.SideOwner[i];
            int playerIndex = lobby.Players.FindIndex(p => p.PeerId == owner);
            _slots[i].Selected = owner switch
            {
                LobbyState.EmptySide => 0,
                LobbyState.BotSide => 1,
                _ => playerIndex >= 0 ? playerIndex + 2 : 1,
            };
            _slots[i].Disabled = !isHost;
        }

        _start.Visible = isHost;
        _start.Text = "Start match";
        _back.Text = isHost ? "Close lobby" : "Leave lobby";
        _setupStatus.Text = status;
        _refreshing = false;
    }

    public void ReadLocalConfig(MatchConfig cfg)
    {
        cfg.TeamMode = (TeamMode)_teamMode.Selected;
        cfg.Layout = (TeamLayout)_layout.Selected;
        cfg.LivesMode = (LivesMode)_livesMode.Selected;
        cfg.BallMode = (BallMode)_ballMode.Selected;
        cfg.Lives = (int)_lives.Value;
        cfg.RoundsToWin = (int)_rounds.Value;
        cfg.MaxBalls = (int)_maxBalls.Value;
        for (int i = 0; i < 4; i++)
            cfg.Slots[i] = (ControllerKind)_slots[i].Selected;
    }

    public void ReadLobbySettings(LobbyState lobby)
    {
        lobby.TeamMode = (TeamMode)_teamMode.Selected;
        lobby.Layout = (TeamLayout)_layout.Selected;
        lobby.LivesMode = (LivesMode)_livesMode.Selected;
        lobby.BallMode = (BallMode)_ballMode.Selected;
        lobby.Lives = (int)_lives.Value;
        lobby.RoundsToWin = (int)_rounds.Value;
        lobby.MaxBalls = (int)_maxBalls.Value;
    }

    // ------------------------------------------------------------------ building

    Control BuildMainPanel()
    {
        var (root, vbox) = MakePanel();

        AddTitle(vbox, "LAST GUARDIAN STANDING");
        vbox.AddChild(new Label { Text = "Four-way paddle ball", HorizontalAlignment = HorizontalAlignment.Center });
        vbox.AddChild(new HSeparator());

        var nameRow = new HBoxContainer();
        nameRow.AddChild(new Label { Text = "Your name", CustomMinimumSize = new Vector2(110, 0) });
        _name = new LineEdit { Text = "Player", FocusMode = Control.FocusModeEnum.Click, MaxLength = LobbyState.MaxNameLength, CustomMinimumSize = new Vector2(260, 0) };
        nameRow.AddChild(_name);
        vbox.AddChild(nameRow);

        _localBtn = AddButton(vbox, "Local match (shared keyboard / gamepads)", () => LocalPressed?.Invoke());
        vbox.AddChild(new HSeparator());

        var hostRow = new HBoxContainer();
        hostRow.AddThemeConstantOverride("separation", 10);
        _hostBtn = new Button { Text = "Host online game", CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _hostBtn.Pressed += () => HostPressed?.Invoke();
        hostRow.AddChild(_hostBtn);
        hostRow.AddChild(new Label { Text = "Port" });
        _port = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = Protocol.DefaultPort, Step = 1, Rounded = true };
        hostRow.AddChild(_port);
        vbox.AddChild(hostRow);

        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 10);
        _address = new LineEdit { Text = "127.0.0.1", FocusMode = Control.FocusModeEnum.Click, PlaceholderText = "Host address", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        joinRow.AddChild(_address);
        _joinBtn = new Button { Text = "Join game", CustomMinimumSize = new Vector2(140, 40) };
        _joinBtn.Pressed += () => JoinPressed?.Invoke();
        joinRow.AddChild(_joinBtn);
        vbox.AddChild(joinRow);

        vbox.AddChild(new HSeparator());
        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 16);
        _controlsBtn = new Button { Text = "Controls & gamepads", CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _controlsBtn.Pressed += ShowControls;
        bottomRow.AddChild(_controlsBtn);
        _music = new CheckButton { Text = "Music", ButtonPressed = true };
        _music.Toggled += on => MusicToggled?.Invoke(on);
        bottomRow.AddChild(_music);
        vbox.AddChild(bottomRow);

        _mainStatus = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(420, 0),
            Modulate = new Color(1f, 0.85f, 0.5f),
        };
        vbox.AddChild(_mainStatus);
        return root;
    }

    Control BuildSetupPanel()
    {
        var (root, vbox) = MakePanel();

        _setupTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _setupTitle.AddThemeFontSizeOverride("font_size", 30);
        _setupTitle.AddThemeColorOverride("font_color", ArenaView.TeamColors[0]);
        vbox.AddChild(_setupTitle);

        _players = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) };
        vbox.AddChild(_players);
        vbox.AddChild(new HSeparator());

        var grid = MakeGrid(vbox);
        _teamMode = AddOption(grid, "Teams", "Free-for-all (1v1v1v1)", "2 v 2", "1 v 1 v 2", "1 v 3");
        _layout = AddOption(grid, "Team layout", "Random", "Side by side", "Facing each other");
        _livesMode = AddOption(grid, "Lives", "Personal", "Shared per team");
        _ballMode = AddOption(grid, "Balls", "Single", "Chaos");
        _lives = AddSpin(grid, "Lives each", 1, 20);
        _rounds = AddSpin(grid, "Rounds to win", 1, 9);
        _maxBalls = AddSpin(grid, "Max balls (chaos)", 2, 10);

        vbox.AddChild(new HSeparator());
        var slots = MakeGrid(vbox);
        for (int i = 0; i < 4; i++)
        {
            int side = i;
            _slotLabels[i] = new Label();
            slots.AddChild(_slotLabels[i]);
            _slots[i] = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            _slots[i].ItemSelected += idx => { if (!_refreshing) SlotEdited?.Invoke(side, (int)idx); };
            slots.AddChild(_slots[i]);
        }

        vbox.AddChild(new HSeparator());
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        _back = new Button { Text = "Back", CustomMinimumSize = new Vector2(160, 44) };
        _back.Pressed += () => BackPressed?.Invoke();
        buttons.AddChild(_back);
        _start = new Button { Text = "Start match", CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _start.Pressed += () => StartPressed?.Invoke();
        buttons.AddChild(_start);
        vbox.AddChild(buttons);

        _setupStatus = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(460, 0),
            Modulate = new Color(1, 1, 1, 0.7f),
        };
        vbox.AddChild(_setupStatus);
        return root;
    }

    // ------------------------------------------------------------------ controls screen

    const string DefaultControlsHint = "Click a box, then press a key.  Right-click a box to clear it.";

    static string LocalControlsHint(int side)
    {
        int pad = Controls.LocalPad[side];
        string keys = Controls.LocalKeysHint(side);
        return pad >= 0 ? $"{keys} + Pad {pad + 1}" : keys;
    }

    Control BuildControlsPanel()
    {
        var (root, vbox) = MakePanel();
        vbox.AddThemeConstantOverride("separation", 8);

        var title = new Label { Text = "Controls", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", ArenaView.TeamColors[0]);
        vbox.AddChild(title);

        _controlsStatus = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(640, 0),
            Modulate = new Color(1f, 0.85f, 0.5f),
        };
        vbox.AddChild(_controlsStatus);

        AddSection(vbox, "Local match — each side's keys and gamepad");
        var local = new GridContainer { Columns = 6 };
        local.AddThemeConstantOverride("h_separation", 10);
        local.AddThemeConstantOverride("v_separation", 6);
        vbox.AddChild(local);
        local.AddChild(new Label());
        foreach (var dir in Controls.DirNames) local.AddChild(HeaderLabel(dir));
        local.AddChild(HeaderLabel("Gamepad"));
        for (int side = 0; side < 4; side++)
        {
            local.AddChild(new Label { Text = ArenaView.SideNames[side], CustomMinimumSize = new Vector2(70, 0) });
            for (int dir = 0; dir < 4; dir++)
                local.AddChild(MakeBindButton(Controls.LocalId(side, dir), 0));

            int s = side;
            var picker = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
            picker.AddItem("Auto");
            picker.AddItem("None");
            for (int pad = 0; pad < Controls.MaxPads; pad++) picker.AddItem($"Pad {pad + 1}");
            picker.ItemSelected += idx => Controls.SetLocalPad(s, (int)idx switch
            {
                0 => Controls.PadAuto,
                1 => Controls.PadNone,
                var i => i - 2,
            });
            _padPickers[side] = picker;
            local.AddChild(picker);
        }

        AddSection(vbox, "Online — your goal is always at the bottom of your screen");
        var online = MakeGrid(vbox, columns: 3);
        foreach (var id in new[] { Controls.OnlineLeft, Controls.OnlineRight })
        {
            online.AddChild(new Label { Text = Controls.Action(id).Label });
            online.AddChild(MakeBindButton(id, 0));
            online.AddChild(MakeBindButton(id, 1));
        }

        AddSection(vbox, "During a match");
        var match = MakeGrid(vbox, columns: 2);
        foreach (var (id, label) in new[]
                 {
                     (Controls.Exit, "Leave the match"),
                     (Controls.Menu, "Back to menu / lobby (match over)"),
                     (Controls.Rematch, "Rematch (match over)"),
                     (Controls.Pause, "Pause (local match)"),
                 })
        {
            match.AddChild(new Label { Text = label });
            match.AddChild(MakeBindButton(id, 0));
        }

        AddSection(vbox, "Gamepads");
        vbox.AddChild(new Label
        {
            Text = "Left stick or d-pad moves.  Start: pause (local) / rematch.  Back / Create: leave.\n"
                 + "B / Circle: menu (match over).  In menus: A / Cross presses, B / Circle goes back.\n"
                 + "Online, any gamepad steers your paddle.  Locally, \"Auto\" hands pads to human sides in turn.",
            Modulate = new Color(1, 1, 1, 0.75f),
        });
        _padList = new Label { Modulate = new Color(1, 1, 1, 0.75f) };
        vbox.AddChild(_padList);

        vbox.AddChild(new HSeparator());
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        var reset = new Button { Text = "Reset to defaults", CustomMinimumSize = new Vector2(200, 44) };
        reset.Pressed += () =>
        {
            Controls.ResetToDefaults();
            _listening = null;
            _controlsStatus.Text = "Controls reset to defaults.";
            RefreshControls();
        };
        buttons.AddChild(reset);
        var done = new Button { Text = "Done", CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        done.Pressed += () => ShowMain();
        buttons.AddChild(done);
        vbox.AddChild(buttons);
        return root;
    }

    Button MakeBindButton(string id, int slot)
    {
        var b = new Button { CustomMinimumSize = new Vector2(110, 34), ClipText = true };
        b.Pressed += () =>
        {
            // Clicking the box that's waiting for a key cancels; any other box starts waiting.
            _listening = _listening == (id, slot) ? null : (id, slot);
            _controlsStatus.Text = _listening == null ? DefaultControlsHint : $"Press a key for \"{Controls.Action(id).Label}\"…";
            RefreshControls();
        };
        b.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
            Controls.Clear(id, slot);
            _listening = null;
            _controlsStatus.Text = DefaultControlsHint;
            RefreshControls();
            b.AcceptEvent();
        };
        _bindButtons.Add((b, id, slot));
        return b;
    }

    void RefreshControls()
    {
        if (_controlsPanel == null) return;
        foreach (var (button, id, slot) in _bindButtons)
        {
            bool waiting = _listening == (id, slot);
            button.Text = waiting ? "…" : Controls.KeyName(Controls.KeysOf(id)[slot]);
            button.Modulate = waiting ? new Color(1f, 0.85f, 0.5f) : Colors.White;
        }
        for (int side = 0; side < 4; side++)
        {
            int pad = Controls.LocalPad[side];
            _padPickers[side].Selected = pad switch { Controls.PadAuto => 0, Controls.PadNone => 1, _ => pad + 2 };
        }

        var pads = Input.GetConnectedJoypads();
        _padList.Text = pads.Count == 0
            ? "No gamepad connected."
            : "Connected: " + string.Join(",  ", pads.Select((d, i) => $"Pad {i + 1}: {Input.GetJoyName(d)}"));
    }

    static void AddSection(VBoxContainer box, string text)
    {
        box.AddChild(new HSeparator());
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", ArenaView.TeamColors[0]);
        box.AddChild(l);
    }

    static Label HeaderLabel(string text) =>
        new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.7f) };

    void WriteSettings(TeamMode teamMode, TeamLayout layout, LivesMode livesMode, BallMode ballMode, int lives, int rounds, int maxBalls)
    {
        _teamMode.Selected = (int)teamMode;
        _layout.Selected = (int)layout;
        _livesMode.Selected = (int)livesMode;
        _ballMode.Selected = (int)ballMode;
        _lives.Value = lives;
        _rounds.Value = rounds;
        _maxBalls.Value = maxBalls;
    }

    void SetSettingsEditable(bool editable)
    {
        _teamMode.Disabled = _layout.Disabled = _livesMode.Disabled = _ballMode.Disabled = !editable;
        _lives.Editable = _rounds.Editable = _maxBalls.Editable = editable;
    }

    (Control root, VBoxContainer content) MakePanel()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 28);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);
        return (center, vbox);
    }

    static void AddTitle(VBoxContainer box, string text)
    {
        var title = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 44);
        title.AddThemeColorOverride("font_color", ArenaView.TeamColors[0]);
        box.AddChild(title);
    }

    static Button AddButton(VBoxContainer box, string text, Action onPressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 40) };
        b.Pressed += onPressed;
        box.AddChild(b);
        return b;
    }

    static GridContainer MakeGrid(VBoxContainer box, int columns = 2)
    {
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(grid);
        return grid;
    }

    OptionButton AddOption(GridContainer grid, string label, params string[] items)
    {
        grid.AddChild(new Label { Text = label });
        var ob = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        foreach (var item in items) ob.AddItem(item);
        ob.ItemSelected += _ => { if (!_refreshing) SettingsEdited?.Invoke(); };
        grid.AddChild(ob);
        return ob;
    }

    SpinBox AddSpin(GridContainer grid, string label, int min, int max)
    {
        grid.AddChild(new Label { Text = label });
        var sb = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Rounded = true };
        sb.ValueChanged += _ => { if (!_refreshing) SettingsEdited?.Invoke(); };
        grid.AddChild(sb);
        return sb;
    }
}
