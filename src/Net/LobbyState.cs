using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Net;

public sealed class LobbyPlayer
{
    public long PeerId;
    public string Name = "";
}

/// <summary>
/// Who is connected, who defends which side, and the match settings. Owned by the host and broadcast to
/// everyone whenever it changes.
/// </summary>
public sealed class LobbyState
{
    public const long EmptySide = -1;
    public const long BotSide = 0;
    public const int MaxNameLength = 16;

    /// <summary>Order in which newcomers are seated: South (the host), then North, East, West.</summary>
    static readonly int[] SeatOrder = { 2, 0, 1, 3 };

    public List<LobbyPlayer> Players { get; } = new();

    /// <summary>Per side: a player's peer id, <see cref="BotSide"/> or <see cref="EmptySide"/>.</summary>
    public long[] SideOwner { get; } = { BotSide, BotSide, BotSide, BotSide };

    public TeamMode TeamMode = TeamMode.FreeForAll;
    public TeamLayout Layout = TeamLayout.Random;
    public LivesMode LivesMode = LivesMode.Personal;
    public BallMode BallMode = BallMode.Single;
    public int Lives = 5;
    public int RoundsToWin = 3;
    public int MaxBalls = 10;
    public bool InMatch;

    public int SideOf(long peerId) => peerId > 0 ? Array.IndexOf(SideOwner, peerId) : -1;

    public string NameOf(long peerId) => Players.FirstOrDefault(p => p.PeerId == peerId)?.Name ?? "?";

    public static string CleanName(string name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength];
        return clean.Length == 0 ? "Player" : clean;
    }

    public void AddPlayer(long peerId, string name, bool seat)
    {
        if (Players.Any(p => p.PeerId == peerId)) return;
        Players.Add(new LobbyPlayer { PeerId = peerId, Name = CleanName(name) });
        if (!seat) return;

        // Take a bot's seat first, then an empty one.
        foreach (long wanted in new[] { BotSide, EmptySide })
            foreach (int side in SeatOrder)
                if (SideOwner[side] == wanted)
                {
                    SideOwner[side] = peerId;
                    return;
                }
    }

    public void RemovePlayer(long peerId)
    {
        Players.RemoveAll(p => p.PeerId == peerId);
        for (int i = 0; i < 4; i++)
            if (SideOwner[i] == peerId)
                SideOwner[i] = BotSide;
    }

    /// <summary>Puts <paramref name="owner"/> on a side. A player can only hold one side at a time.</summary>
    public void Assign(int side, long owner)
    {
        if (owner > 0)
            for (int i = 0; i < 4; i++)
                if (SideOwner[i] == owner)
                    SideOwner[i] = BotSide;
        SideOwner[side] = owner;
    }

    public MatchConfig ToConfig()
    {
        var cfg = new MatchConfig
        {
            TeamMode = TeamMode, Layout = Layout, LivesMode = LivesMode, BallMode = BallMode,
            Lives = Lives, RoundsToWin = RoundsToWin, MaxBalls = MaxBalls,
        };
        for (int i = 0; i < 4; i++)
            cfg.Slots[i] = SideOwner[i] switch
            {
                EmptySide => ControllerKind.Empty,
                BotSide => ControllerKind.Bot,
                _ => ControllerKind.Human,
            };
        return cfg;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Players.Count);
        foreach (var p in Players)
        {
            w.Write(p.PeerId);
            w.Write(p.Name);
        }
        foreach (long owner in SideOwner) w.Write(owner);
        w.Write((byte)TeamMode);
        w.Write((byte)Layout);
        w.Write((byte)LivesMode);
        w.Write((byte)BallMode);
        w.Write((byte)Lives);
        w.Write((byte)RoundsToWin);
        w.Write((byte)MaxBalls);
        w.Write(InMatch);
        w.Flush();
        return ms.ToArray();
    }

    public static LobbyState? Deserialize(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var s = new LobbyState();
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
                s.Players.Add(new LobbyPlayer { PeerId = r.ReadInt64(), Name = CleanName(r.ReadString()) });
            for (int i = 0; i < 4; i++) s.SideOwner[i] = r.ReadInt64();
            s.TeamMode = (TeamMode)r.ReadByte();
            s.Layout = (TeamLayout)r.ReadByte();
            s.LivesMode = (LivesMode)r.ReadByte();
            s.BallMode = (BallMode)r.ReadByte();
            s.Lives = r.ReadByte();
            s.RoundsToWin = r.ReadByte();
            s.MaxBalls = r.ReadByte();
            s.InMatch = r.ReadBoolean();
            return s;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException)
        {
            return null;
        }
    }
}
