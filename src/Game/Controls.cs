using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace LastGuardianStanding.Game;

/// <summary>Which keys may not share a binding: local and online keys never apply at the same time.</summary>
public enum KeyGroup { Local, Online, Match }

/// <param name="Slots">How many keys the action can hold (e.g. A and Left both move left online).</param>
public sealed record ControlAction(string Id, string Label, KeyGroup Group, Key[] Defaults)
{
    public int Slots => Defaults.Length;
}

/// <summary>
/// Player controls: rebindable keyboard keys (saved to user://controls.cfg) and gamepads.
/// Keys are stored as physical keys, so a binding stays in the same place on AZERTY / QWERTZ keyboards.
/// Gamepads: left stick or d-pad moves; Start pauses (local) or rematches once it's over; Back leaves
/// the match; B = menu once it's over. In menus: A / ✕ presses, B / ○ goes back.
/// </summary>
public static class Controls
{
    public const int PadAuto = -2;
    public const int PadNone = -1;
    public const int MaxPads = 4;

    public const string OnlineLeft = "online_left";
    public const string OnlineRight = "online_right";
    public const string Exit = "match_exit";
    public const string Menu = "match_menu";
    public const string Rematch = "match_rematch";
    public const string Pause = "match_pause";

    public static readonly string[] DirNames = { "Up", "Left", "Down", "Right" };

    /// <summary>Order in which "Auto" gamepads go to human sides: the usual seats first.</summary>
    static readonly int[] PadSeatOrder = { 2, 0, 1, 3 };

    // Up, left, down, right per side (N, E, S, W).
    static readonly Key[][] LocalDefaults =
    {
        new[] { Key.W, Key.A, Key.S, Key.D },
        new[] { Key.Kp8, Key.Kp4, Key.Kp5, Key.Kp6 },
        new[] { Key.Up, Key.Left, Key.Down, Key.Right },
        new[] { Key.I, Key.J, Key.K, Key.L },
    };

    const string SavePath = "user://controls.cfg";
    const float DeadZone = 0.25f;

    public static readonly ControlAction[] Actions = BuildActions();
    static readonly Dictionary<string, ControlAction> ById = Actions.ToDictionary(a => a.Id);
    static readonly Dictionary<string, Key[]> Bound = new();

    /// <summary>Gamepad per local side: <see cref="PadAuto"/>, <see cref="PadNone"/> or a pad number (0-based).</summary>
    public static readonly int[] LocalPad = { PadAuto, PadAuto, PadAuto, PadAuto };

    static Controls() => ResetToDefaults(save: false);

    public static string LocalId(int side, int dir) => $"local_{side}_{dir}";

    static ControlAction[] BuildActions()
    {
        var list = new List<ControlAction>();
        for (int side = 0; side < 4; side++)
            for (int dir = 0; dir < 4; dir++)
                list.Add(new ControlAction(LocalId(side, dir), $"{ArenaView.SideNames[side]} {DirNames[dir]}",
                    KeyGroup.Local, new[] { LocalDefaults[side][dir] }));
        list.Add(new ControlAction(OnlineLeft, "Move left", KeyGroup.Online, new[] { Key.A, Key.Left }));
        list.Add(new ControlAction(OnlineRight, "Move right", KeyGroup.Online, new[] { Key.D, Key.Right }));
        list.Add(new ControlAction(Exit, "Leave match", KeyGroup.Match, new[] { Key.Escape }));
        list.Add(new ControlAction(Menu, "Menu (match over)", KeyGroup.Match, new[] { Key.Space }));
        list.Add(new ControlAction(Rematch, "Rematch", KeyGroup.Match, new[] { Key.R }));
        list.Add(new ControlAction(Pause, "Pause", KeyGroup.Match, new[] { Key.P }));
        return list.ToArray();
    }

    /// <summary>
    /// Godot's built-in menu actions only know the d-pad / stick for moving between buttons: "accept" and
    /// "back" have no gamepad buttons. Add A (PlayStation ✕) and B (PlayStation ○), for every pad.
    /// </summary>
    public static void AddGamepadMenuButtons()
    {
        AddPadButton("ui_accept", JoyButton.A);
        AddPadButton("ui_cancel", JoyButton.B);
    }

    static void AddPadButton(string action, JoyButton button)
    {
        var e = new InputEventJoypadButton { ButtonIndex = button, Device = -1 };
        if (!InputMap.ActionHasEvent(action, e)) InputMap.ActionAddEvent(action, e);
    }

    // ------------------------------------------------------------------ bindings

    public static Key[] KeysOf(string id) => Bound[id];
    public static ControlAction Action(string id) => ById[id];

    /// <summary>
    /// Binds <paramref name="key"/> to one slot of an action, removing it from actions it would clash with.
    /// Returns the labels of the actions that lost the key.
    /// </summary>
    public static List<string> Assign(string id, int slot, Key key)
    {
        var action = ById[id];
        var removedFrom = new List<string>();
        foreach (var other in Actions)
        {
            if (!Clashes(action.Group, other.Group)) continue;
            var keys = Bound[other.Id];
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] != key || (other.Id == id && i == slot)) continue;
                keys[i] = Key.None;
                if (other.Id != id) removedFrom.Add(other.Label);
            }
        }
        Bound[id][slot] = key;
        Save();
        return removedFrom;
    }

    public static void Clear(string id, int slot)
    {
        Bound[id][slot] = Key.None;
        Save();
    }

    public static void SetLocalPad(int side, int pad)
    {
        LocalPad[side] = pad;
        Save();
    }

    static bool Clashes(KeyGroup a, KeyGroup b) => a == b || a == KeyGroup.Match || b == KeyGroup.Match;

    public static void ResetToDefaults(bool save = true)
    {
        foreach (var a in Actions) Bound[a.Id] = (Key[])a.Defaults.Clone();
        Array.Fill(LocalPad, PadAuto);
        if (save) Save();
    }

    // ------------------------------------------------------------------ reading

    static Key KeyOf(InputEventKey e) => e.PhysicalKeycode != Key.None ? e.PhysicalKeycode : e.Keycode;

    /// <summary>The key to store for a key press (physical where the platform reports it).</summary>
    public static Key BindableKey(InputEventKey e) => KeyOf(e);

    public static bool Matches(string id, InputEventKey e)
    {
        var key = KeyOf(e);
        return key != Key.None && Array.IndexOf(Bound[id], key) >= 0;
    }

    static bool Held(string id)
    {
        foreach (var k in Bound[id])
            if (k != Key.None && Input.IsPhysicalKeyPressed(k)) return true;
        return false;
    }

    /// <summary>Local play: keyboard plus this side's gamepad (if any), in screen directions.</summary>
    public static Vector2 LocalIntent(int side, int device)
    {
        float x = (Held(LocalId(side, 3)) ? 1 : 0) - (Held(LocalId(side, 1)) ? 1 : 0);
        float y = (Held(LocalId(side, 2)) ? 1 : 0) - (Held(LocalId(side, 0)) ? 1 : 0);
        var v = new Vector2(x, y);
        if (device >= 0) v += PadVector(device);
        return v.LimitLength(1f);
    }

    /// <summary>Online: left/right on screen, from the keyboard and every connected gamepad.</summary>
    public static float OnlineAxis()
    {
        float x = (Held(OnlineRight) ? 1 : 0) - (Held(OnlineLeft) ? 1 : 0);
        foreach (int device in Input.GetConnectedJoypads())
            x += PadVector(device).X;
        return Math.Clamp(x, -1f, 1f);
    }

    /// <summary>Left stick (with a dead zone, so a resting stick doesn't drift) overridden by the d-pad.</summary>
    public static Vector2 PadVector(int device)
    {
        var v = new Vector2(Input.GetJoyAxis(device, JoyAxis.LeftX), Input.GetJoyAxis(device, JoyAxis.LeftY));
        float len = v.Length();
        v = len < DeadZone ? Vector2.Zero : v / len * Math.Min(1f, (len - DeadZone) / (1f - DeadZone));

        if (Input.IsJoyButtonPressed(device, JoyButton.DpadLeft)) v.X = -1f;
        else if (Input.IsJoyButtonPressed(device, JoyButton.DpadRight)) v.X = 1f;
        if (Input.IsJoyButtonPressed(device, JoyButton.DpadUp)) v.Y = -1f;
        else if (Input.IsJoyButtonPressed(device, JoyButton.DpadDown)) v.Y = 1f;
        return v;
    }

    /// <summary>
    /// Gamepad device for each local side (-1 = none). Sides with a fixed pad number get that pad if it's
    /// plugged in; the remaining pads go to "Auto" human sides in seat order (South, North, East, West).
    /// </summary>
    public static int[] ResolveLocalPads(Func<int, bool> isHuman)
    {
        var pads = Input.GetConnectedJoypads();
        var result = new[] { -1, -1, -1, -1 };
        var claimed = new HashSet<int>();
        for (int side = 0; side < 4; side++)
        {
            int pad = LocalPad[side];
            if (pad >= 0 && pad < pads.Count && claimed.Add(pad))
                result[side] = pads[pad];
        }

        int next = 0;
        foreach (int side in PadSeatOrder)
        {
            if (LocalPad[side] != PadAuto || !isHuman(side)) continue;
            while (next < pads.Count && claimed.Contains(next)) next++;
            if (next >= pads.Count) break;
            claimed.Add(next);
            result[side] = pads[next++];
        }
        return result;
    }

    /// <summary>"Pad 2" for a device id, numbered like the Controls screen.</summary>
    public static string PadName(int device)
    {
        int index = Input.GetConnectedJoypads().IndexOf(device);
        return index >= 0 ? $"Pad {index + 1}" : "Pad";
    }

    // ------------------------------------------------------------------ names

    static readonly bool LayoutAware = OS.HasFeature("pc") && DisplayServer.GetName() != "headless";

    /// <summary>The key's name as printed on this keyboard layout.</summary>
    public static string KeyName(Key key)
    {
        if (key == Key.None) return "—";
        // Layout lookup only exists on desktop display servers (not headless / mobile).
        var label = LayoutAware ? DisplayServer.KeyboardGetLabelFromPhysical(key) : Key.None;
        string name = OS.GetKeycodeString(label != Key.None ? label : key);
        if (name.StartsWith("Kp ")) name = "Num " + name[3..];
        return name.Length > 0 ? name : key.ToString();
    }

    /// <summary>All of an action's keys, e.g. "A / Left".</summary>
    public static string KeysText(string id)
    {
        var names = Bound[id].Where(k => k != Key.None).Select(KeyName).ToList();
        return names.Count > 0 ? string.Join(" / ", names) : "unbound";
    }

    /// <summary>Short hint for a local side's keys: "W A S D", "Arrow keys", "Numpad 8 4 5 6".</summary>
    public static string LocalKeysHint(int side)
    {
        var keys = Enumerable.Range(0, 4).Select(d => Bound[LocalId(side, d)][0]).ToArray();
        if (keys.SequenceEqual(new[] { Key.Up, Key.Left, Key.Down, Key.Right })) return "Arrow keys";
        if (keys.All(k => k == Key.None)) return "no keys";
        var names = keys.Select(KeyName).ToArray();
        if (names.All(n => n.StartsWith("Num ")))
            return "Numpad " + string.Join(" ", names.Select(n => n[4..]));
        return string.Join(" ", names);
    }

    // ------------------------------------------------------------------ saving

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) != Error.Ok) return;

        foreach (var a in Actions)
        {
            if (!cfg.HasSectionKey("keys", a.Id)) continue;
            var parts = cfg.GetValue("keys", a.Id).AsString().Split(',');
            var keys = new Key[a.Slots];
            for (int i = 0; i < keys.Length && i < parts.Length; i++)
                keys[i] = long.TryParse(parts[i], out var k) ? (Key)k : Key.None;
            Bound[a.Id] = keys;
        }
        for (int side = 0; side < 4; side++)
        {
            int pad = cfg.GetValue("gamepad", $"local_{side}", PadAuto).AsInt32();
            LocalPad[side] = pad >= PadAuto && pad < MaxPads ? pad : PadAuto;
        }
    }

    static void Save()
    {
        var cfg = new ConfigFile();
        foreach (var a in Actions)
            cfg.SetValue("keys", a.Id, string.Join(",", Bound[a.Id].Select(k => (long)k)));
        for (int side = 0; side < 4; side++)
            cfg.SetValue("gamepad", $"local_{side}", LocalPad[side]);
        cfg.Save(SavePath);
    }
}
