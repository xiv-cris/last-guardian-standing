using System;
using System.Linq;
using Godot;
using LastGuardianStanding.Net;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Game;

/// <summary>
/// All menu screens, built in code: the main screen (name, local / host / join) and one setup panel that
/// serves as both the local match setup and the online lobby. Raises events; holds no game logic.
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

    static readonly string[] KeyHints = { "W A S D", "Numpad 8 4 5 6", "Arrow keys", "I J K L" };

    Control _mainPanel = null!;
    Control _setupPanel = null!;

    LineEdit _name = null!;
    LineEdit _address = null!;
    SpinBox _port = null!;
    Label _mainStatus = null!;
    Button _localBtn = null!, _hostBtn = null!, _joinBtn = null!;

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
    public string PlayerName => LobbyState.CleanName(_name.Text);
    public string Address => _address.Text.Trim();
    public int Port => (int)_port.Value;

    public void SetPlayerName(string name) => _name.Text = name;
    public void SetAddress(string address) => _address.Text = address;
    public void SetPort(int port) => _port.Value = port;

    public override void _Ready()
    {
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);

        _mainPanel = BuildMainPanel();
        _setupPanel = BuildSetupPanel();
    }

    // ------------------------------------------------------------------ public screens

    public void ShowMain(string status = "", bool busy = false)
    {
        Visible = true;
        _mainPanel.Visible = true;
        _setupPanel.Visible = false;
        _mainStatus.Text = status;
        _localBtn.Disabled = _hostBtn.Disabled = _joinBtn.Disabled = busy;
    }

    public void ShowLocalSetup(MatchConfig cfg)
    {
        _refreshing = true;
        Visible = true;
        _mainPanel.Visible = false;
        _setupPanel.Visible = true;

        _setupTitle.Text = "Local match";
        _players.Visible = false;
        WriteSettings(cfg.TeamMode, cfg.Layout, cfg.LivesMode, cfg.BallMode, cfg.Lives, cfg.RoundsToWin, cfg.MaxBalls);
        SetSettingsEditable(true);

        for (int i = 0; i < 4; i++)
        {
            _slotLabels[i].Text = $"{ArenaView.SideNames[i]}   ({KeyHints[i]})";
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
        Visible = true;
        _mainPanel.Visible = false;
        _setupPanel.Visible = true;

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
        _name = new LineEdit { Text = "Player", MaxLength = LobbyState.MaxNameLength, CustomMinimumSize = new Vector2(260, 0) };
        nameRow.AddChild(_name);
        vbox.AddChild(nameRow);

        _localBtn = AddButton(vbox, "Local match (one keyboard)", () => LocalPressed?.Invoke());
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
        _address = new LineEdit { Text = "127.0.0.1", PlaceholderText = "Host address", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        joinRow.AddChild(_address);
        _joinBtn = new Button { Text = "Join game", CustomMinimumSize = new Vector2(140, 40) };
        _joinBtn.Pressed += () => JoinPressed?.Invoke();
        joinRow.AddChild(_joinBtn);
        vbox.AddChild(joinRow);

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

    static GridContainer MakeGrid(VBoxContainer box)
    {
        var grid = new GridContainer { Columns = 2 };
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
