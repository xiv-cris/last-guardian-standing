using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using LastGuardianStanding.Net;
using LastGuardianStanding.Sim;
using NVec = System.Numerics.Vector2;

namespace LastGuardianStanding.Game;

/// <summary>
/// Glue between menus, the simulation, the network and the view. Three ways to play:
/// <list type="bullet">
/// <item>Local: one keyboard, everything runs here.</item>
/// <item>Host: runs the real simulation, applies remote players' paddles, broadcasts snapshots.</item>
/// <item>Client: sends its own paddle position, draws the host's snapshots (smoothed).</item>
/// </list>
/// </summary>
public partial class Main : Node2D
{
    enum Mode { Menu, Local, Host, Client }

    static readonly string[] KeyHints = { "W A S D", "Numpad 8 4 5 6", "Arrow keys", "I J K L" };

    // up, left, down, right — screen directions, which are also world directions in local play.
    static readonly Key[][] SideKeys =
    {
        new[] { Key.W, Key.A, Key.S, Key.D },
        new[] { Key.Kp8, Key.Kp4, Key.Kp5, Key.Kp6 },
        new[] { Key.Up, Key.Left, Key.Down, Key.Right },
        new[] { Key.I, Key.J, Key.K, Key.L },
    };

    Mode _mode = Mode.Menu;
    bool _inMatch;
    ArenaView _view = null!;
    MenuUI _menu = null!;
    NetSession _net = null!;

    readonly MatchConfig _localConfig = new();
    Simulation? _sim;
    GameState? _state;
    int _matchId;

    // Online
    LobbyState _lobby = new();
    readonly List<SimEvent> _pendingEvents = new();
    readonly SnapshotBuffer _buffer = new();
    int _mySide = -1;
    float _myLateral;
    int _myRound = -1;
    int _tickCounter;

    // Command-line helpers (testing)
    double _uptime;
    double _autostartAt = -1;
    string? _screenshotPath;
    double _screenshotAt = -1;

    public override void _Ready()
    {
        _view = new ArenaView { Name = "ArenaView" };
        AddChild(_view);
        _net = new NetSession { Name = "NetSession" };
        AddChild(_net);
        _menu = new MenuUI { Name = "Menu" };
        AddChild(_menu);

        _menu.LocalPressed += () => { _mode = Mode.Local; _menu.ShowLocalSetup(_localConfig); };
        _menu.HostPressed += HostGame;
        _menu.JoinPressed += JoinGame;
        _menu.StartPressed += OnStartPressed;
        _menu.BackPressed += OnBackPressed;
        _menu.SettingsEdited += OnLobbySettingsEdited;
        _menu.SlotEdited += OnLobbySlotEdited;

        _net.ConnectedToHost += () => _net.SendHello(_menu.PlayerName);
        _net.Disconnected += reason => LeaveOnline(reason);
        _net.LobbyReceived += OnLobbyReceived;
        _net.MatchStartReceived += id => EnterClientMatch(id);
        _net.ReturnToLobbyReceived += OnReturnToLobbyReceived;
        _net.SnapshotReceived += OnSnapshotReceived;
        _net.HelloReceived += OnHello;
        _net.PeerLeft += OnPeerLeft;
        _net.InputReceived += OnRemoteInput;

        _menu.ShowMain();
        HandleCommandLine();
    }

    // ------------------------------------------------------------------ loop

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        switch (_mode)
        {
            case Mode.Local when _sim != null:
                for (int i = 0; i < 4; i++)
                    if (_sim.Players[i].Kind == ControllerKind.Human)
                        _sim.SetIntent((GoalSide)i, ReadSideKeys(i));
                _sim.Step(dt);
                _view.OnEvents(_sim.Events);
                _state = GameState.Capture(_sim, _matchId, _sim.Events);
                break;

            case Mode.Host when _inMatch && _sim != null:
                if (_mySide >= 0)
                    _sim.SetIntent((GoalSide)_mySide, ReadOnlineIntent());
                _sim.Step(dt);
                _view.OnEvents(_sim.Events);
                _pendingEvents.AddRange(_sim.Events);
                _state = GameState.Capture(_sim, _matchId, _sim.Events);

                if (++_tickCounter % Protocol.SnapshotEveryTicks == 0)
                {
                    _net.SendSnapshot(GameState.Capture(_sim, _matchId, _pendingEvents).Serialize());
                    _pendingEvents.Clear();
                }
                break;

            case Mode.Client when _inMatch:
                var latest = _buffer.Latest;
                if (latest == null) break;
                if (latest.Round != _myRound)
                {
                    _myRound = latest.Round;
                    _myLateral = 0f; // the host recenters paddles every round
                }
                if (_mySide >= 0 && latest.Players[_mySide].IsActive)
                    _myLateral = Simulation.StepLateral((GoalSide)_mySide, _myLateral, ReadOnlineIntent(), dt);
                if (++_tickCounter % Protocol.SnapshotEveryTicks == 0 && _mySide >= 0)
                    _net.SendInput(_myLateral);
                break;
        }
    }

    public override void _Process(double delta)
    {
        _uptime += delta;

        if (_mode == Mode.Client && _inMatch)
        {
            _buffer.Advance(delta);
            var s = _buffer.Sample();
            if (s != null)
            {
                _view.OnEvents(_buffer.TakeDueEvents());
                // Our own paddle is drawn where we put it, not where the (slightly old) snapshot says.
                if (_mySide >= 0 && s.Players[_mySide].IsActive)
                    s.Players[_mySide].Lateral = _myLateral;
                _state = s;
            }
        }

        _view.State = _state;

        if (_autostartAt >= 0 && _uptime >= _autostartAt && _mode == Mode.Host && !_inMatch)
        {
            _autostartAt = -1;
            StartOnlineMatch();
        }

        if (_screenshotPath != null && _uptime >= _screenshotAt)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            _screenshotPath = null;
            GetTree().Quit();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool matchOver = _state?.Phase == MatchPhase.MatchOver;

        switch (_mode)
        {
            case Mode.Local when _sim != null:
                if (key.Keycode == Key.Escape || (matchOver && key.Keycode == Key.Space)) StopLocal();
                else if (matchOver && key.Keycode == Key.R) StartLocal();
                break;

            case Mode.Host when _inMatch:
                if (key.Keycode == Key.Escape || (matchOver && key.Keycode == Key.Space)) ReturnToLobby();
                else if (matchOver && key.Keycode == Key.R) StartOnlineMatch();
                break;

            case Mode.Client when _inMatch:
                if (key.Keycode == Key.Escape) LeaveOnline("You left the game.");
                break;
        }
    }

    NVec ReadSideKeys(int side)
    {
        var k = SideKeys[side];
        float x = (Input.IsPhysicalKeyPressed(k[3]) ? 1 : 0) - (Input.IsPhysicalKeyPressed(k[1]) ? 1 : 0);
        float y = (Input.IsPhysicalKeyPressed(k[2]) ? 1 : 0) - (Input.IsPhysicalKeyPressed(k[0]) ? 1 : 0);
        return new NVec(x, y);
    }

    /// <summary>Online, everyone's goal is at the bottom: left/right on screen, turned back into world space.</summary>
    NVec ReadOnlineIntent()
    {
        float x = 0f;
        if (Input.IsPhysicalKeyPressed(Key.Left) || Input.IsPhysicalKeyPressed(Key.A)) x -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.Right) || Input.IsPhysicalKeyPressed(Key.D)) x += 1f;
        var world = new Vector2(x, 0f).Rotated(-_view.ViewRotation);
        return new NVec(world.X, world.Y);
    }

    // ------------------------------------------------------------------ local play

    void OnStartPressed()
    {
        if (_mode == Mode.Local)
        {
            _menu.ReadLocalConfig(_localConfig);
            StartLocal();
        }
        else if (_mode == Mode.Host)
        {
            StartOnlineMatch();
        }
    }

    void OnBackPressed()
    {
        if (_mode == Mode.Local)
        {
            _mode = Mode.Menu;
            _menu.ShowMain();
        }
        else
        {
            LeaveOnline("");
        }
    }

    void StartLocal()
    {
        _mode = Mode.Local;
        _sim = new Simulation(_localConfig);
        _matchId++;
        _state = null;

        _view.ViewRotation = 0f;
        _view.MySide = -1;
        for (int i = 0; i < 4; i++)
        {
            _view.SideLabels[i] = _localConfig.Slots[i] == ControllerKind.Bot
                ? $"{ArenaView.SideNames[i]} · BOT"
                : $"{ArenaView.SideNames[i]} · {KeyHints[i]}";
            _view.DisplayNames[i] = ArenaView.SideNames[i];
        }
        _view.MatchOverHint = "Space: menu     R: rematch";
        _view.ResetEffects();
        HideMenu();
    }

    void StopLocal()
    {
        _sim = null;
        _state = null;
        _menu.ShowLocalSetup(_localConfig);
    }

    // ------------------------------------------------------------------ hosting

    void HostGame()
    {
        int port = _menu.Port;
        var err = _net.Host(port);
        if (err != Error.Ok)
        {
            _menu.ShowMain($"Couldn't host on port {port} ({err}). Is another copy already hosting?");
            return;
        }

        _mode = Mode.Host;
        _inMatch = false;
        _lobby = new LobbyState();
        _lobby.AddPlayer(_net.MyId, _menu.PlayerName, seat: true);
        ShowLobby();
    }

    void OnHello(long peerId, string name, int version)
    {
        if (version != Protocol.Version)
        {
            _net.Kick(peerId, $"Version mismatch (host v{Protocol.Version}, you v{version}). Update your game.");
            return;
        }
        if (_lobby.Players.Count >= 4)
        {
            _net.Kick(peerId, "The lobby is full.");
            return;
        }

        // Joining mid-match: watch now, play from the next match.
        _lobby.AddPlayer(peerId, name, seat: !_inMatch);
        BroadcastLobby();
        RefreshLobbyOrLabels();
    }

    void OnPeerLeft(long peerId)
    {
        int side = _lobby.SideOf(peerId);
        _lobby.RemovePlayer(peerId);
        if (_inMatch && side >= 0)
            _sim?.ConvertToBot((GoalSide)side);
        BroadcastLobby();
        RefreshLobbyOrLabels();
    }

    void OnRemoteInput(long peerId, float lateral)
    {
        if (!_inMatch || _sim == null) return;
        int side = _lobby.SideOf(peerId);
        if (side >= 0 && _sim.Players[side].Kind == ControllerKind.Human)
            _sim.SetTargetLateral((GoalSide)side, lateral);
    }

    void OnLobbySettingsEdited()
    {
        if (_mode != Mode.Host || _inMatch) return;
        _menu.ReadLobbySettings(_lobby);
        BroadcastLobby();
    }

    void OnLobbySlotEdited(int side, int index)
    {
        if (_mode != Mode.Host || _inMatch) return;
        long owner = index switch
        {
            0 => LobbyState.EmptySide,
            1 => LobbyState.BotSide,
            _ when index - 2 < _lobby.Players.Count => _lobby.Players[index - 2].PeerId,
            _ => LobbyState.BotSide,
        };
        _lobby.Assign(side, owner);
        BroadcastLobby();
        ShowLobby();
    }

    void StartOnlineMatch()
    {
        if (_mode != Mode.Host) return;

        _sim = new Simulation(_lobby.ToConfig());
        _matchId++;
        _inMatch = true;
        _lobby.InMatch = true;
        _pendingEvents.Clear();
        _tickCounter = 0;
        _mySide = _lobby.SideOf(_net.MyId);

        BroadcastLobby();
        _net.SendMatchStart(_matchId);
        ConfigureOnlineView(resetEffects: true);
        HideMenu();
    }

    void ReturnToLobby()
    {
        _inMatch = false;
        _sim = null;
        _state = null;
        _lobby.InMatch = false;
        _net.SendReturnToLobby();
        BroadcastLobby();
        ShowLobby();
    }

    void BroadcastLobby() => _net.SendLobby(_lobby.Serialize());

    // ------------------------------------------------------------------ joining

    void JoinGame()
    {
        string address = _menu.Address;
        int port = _menu.Port;
        if (address.Length == 0)
        {
            _menu.ShowMain("Type the host's address first.");
            return;
        }

        var err = _net.Join(address, port);
        if (err != Error.Ok)
        {
            _menu.ShowMain($"Couldn't start connecting ({err}).");
            return;
        }

        _mode = Mode.Client;
        _inMatch = false;
        _lobby = new LobbyState();
        _menu.ShowMain($"Connecting to {address}:{port}…", busy: true);
    }

    void OnLobbyReceived(byte[] data)
    {
        if (_mode != Mode.Client || LobbyState.Deserialize(data) is not { } lobby) return;
        _lobby = lobby;
        _mySide = _lobby.SideOf(_net.MyId);
        RefreshLobbyOrLabels();
    }

    void EnterClientMatch(int matchId)
    {
        if (_mode != Mode.Client) return;
        if (_inMatch && matchId == _buffer.MatchId) return;

        _inMatch = true;
        _buffer.Reset(matchId);
        _state = null;
        _myLateral = 0f;
        _myRound = -1;
        _tickCounter = 0;
        _mySide = _lobby.SideOf(_net.MyId);
        ConfigureOnlineView(resetEffects: true);
        HideMenu();
    }

    void OnSnapshotReceived(byte[] data)
    {
        if (_mode != Mode.Client || GameState.Deserialize(data) is not { } s) return;
        if (!_inMatch || s.MatchId > _buffer.MatchId)
            EnterClientMatch(s.MatchId); // also covers joining mid-match and a missed start message
        _buffer.Add(s);
    }

    void OnReturnToLobbyReceived()
    {
        if (_mode != Mode.Client) return;
        _inMatch = false;
        _state = null;
        ShowLobby();
    }

    // ------------------------------------------------------------------ shared online helpers

    void LeaveOnline(string reason)
    {
        _net.Leave();
        _mode = Mode.Menu;
        _inMatch = false;
        _sim = null;
        _state = null;
        _mySide = -1;
        _lobby = new LobbyState();
        _buffer.Reset(-1);
        _menu.ShowMain(reason);
    }

    void ShowLobby()
    {
        bool isHost = _mode == Mode.Host;
        string status = isHost
            ? $"Hosting on port {_menu.Port}. Friends join with your IP address and that port."
            : "Waiting for the host to start the match…";
        _menu.ShowLobby(_lobby, isHost, _net.MyId, status);
    }

    void RefreshLobbyOrLabels()
    {
        if (_inMatch) ConfigureOnlineView(resetEffects: false);
        else ShowLobby();
    }

    void ConfigureOnlineView(bool resetEffects)
    {
        long me = _net.MyId;
        _view.MySide = _mySide;
        _view.ViewRotation = RotationFor(_mySide);
        for (int i = 0; i < 4; i++)
        {
            long owner = _lobby.SideOwner[i];
            string name = owner > 0 ? _lobby.NameOf(owner) : $"Bot ({ArenaView.SideNames[i]})";
            _view.DisplayNames[i] = name;
            _view.SideLabels[i] = owner > 0 ? (owner == me ? $"{name} (you)" : name) : "BOT";
        }
        _view.MatchOverHint = _mode == Mode.Host ? "Space: lobby     R: rematch" : "Waiting for the host…";
        if (resetEffects) _view.ResetEffects();
    }

    /// <summary>Rotation that puts <paramref name="side"/>'s goal at the bottom of the screen.</summary>
    static float RotationFor(int side) => side switch
    {
        (int)GoalSide.North => Mathf.Pi,
        (int)GoalSide.East => Mathf.Pi / 2f,
        (int)GoalSide.West => -Mathf.Pi / 2f,
        _ => 0f,
    };

    void HideMenu()
    {
        _menu.Visible = false;
        GetViewport().GuiReleaseFocus();
    }

    // ------------------------------------------------------------------ command line

    /// <summary>
    /// Options after "--" on the command line, mainly for testing:
    /// --demo, --name N, --port P, --host, --join ADDRESS, --autostart SECONDS, --screenshot PATH, --screenshot-at SECONDS
    /// </summary>
    void HandleCommandLine()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        double Seconds(string? s, double fallback) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        if (Arg("--name") is { } name) _menu.SetPlayerName(name);
        if (Arg("--port") is { } port && int.TryParse(port, out var p)) _menu.SetPort(p);
        if (Arg("--screenshot") is { } shot)
        {
            _screenshotPath = shot;
            _screenshotAt = Seconds(Arg("--screenshot-at"), 8);
        }
        if (Arg("--autostart") is { } auto) _autostartAt = Seconds(auto, 3);

        if (args.Contains("--demo"))
        {
            for (int i = 0; i < 4; i++) _localConfig.Slots[i] = ControllerKind.Bot;
            _localConfig.BallMode = BallMode.Chaos;
            _localConfig.TeamMode = TeamMode.TwoVsTwo;
            StartLocal();
        }
        else if (args.Contains("--host"))
        {
            HostGame();
        }
        else if (Arg("--join") is { } address)
        {
            _menu.SetAddress(address);
            JoinGame();
        }
    }
}
