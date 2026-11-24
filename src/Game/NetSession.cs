using System;
using Godot;
using LastGuardianStanding.Net;

namespace LastGuardianStanding.Game;

/// <summary>
/// The only place that talks to the network. Wraps Godot's ENet peer and RPCs, and turns them into plain C#
/// events. Must live at the same node path on every peer (/root/Main/NetSession) for RPCs to route.
/// </summary>
public partial class NetSession : Node
{
    // Client side
    public event Action? ConnectedToHost;
    public event Action<string>? Disconnected;
    public event Action<byte[]>? LobbyReceived;
    public event Action<int>? MatchStartReceived;
    public event Action? ReturnToLobbyReceived;
    public event Action<byte[]>? SnapshotReceived;

    // Host side
    public event Action<long, string, int>? HelloReceived;
    public event Action<long>? PeerLeft;
    public event Action<long, float>? InputReceived;

    ENetMultiplayerPeer? _peer;
    string _kickReason = "";

    public bool IsHost { get; private set; }
    public long MyId => Multiplayer.GetUniqueId();

    public override void _Ready()
    {
        Multiplayer.PeerDisconnected += id => { if (IsHost) PeerLeft?.Invoke(id); };
        Multiplayer.ConnectedToServer += () => ConnectedToHost?.Invoke();
        Multiplayer.ConnectionFailed += () => Disconnected?.Invoke("Couldn't reach the host. Check the address, port and firewall.");
        Multiplayer.ServerDisconnected += () =>
            Disconnected?.Invoke(_kickReason.Length > 0 ? _kickReason : "Lost connection to the host.");
    }

    public Error Host(int port)
    {
        CloseNow();
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, Protocol.MaxClients);
        if (err != Error.Ok) return err;
        _peer = peer;
        Multiplayer.MultiplayerPeer = peer;
        IsHost = true;
        return Error.Ok;
    }

    public Error Join(string address, int port)
    {
        CloseNow();
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(address, port);
        if (err != Error.Ok) return err;
        _peer = peer;
        _kickReason = "";
        Multiplayer.MultiplayerPeer = peer;
        IsHost = false;
        return Error.Ok;
    }

    /// <summary>Disconnects. Deferred, because this is often called from inside a network signal.</summary>
    public void Leave() => CallDeferred(MethodName.CloseNow);

    public void CloseNow()
    {
        _peer?.Close();
        _peer = null;
        IsHost = false;
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
    }

    // ------------------------------------------------------------------ host -> clients

    public void SendLobby(byte[] data) => Rpc(MethodName.RpcLobby, data);
    public void SendMatchStart(int matchId) => Rpc(MethodName.RpcMatchStart, matchId);
    public void SendReturnToLobby() => Rpc(MethodName.RpcReturnToLobby);
    public void SendSnapshot(byte[] data) => Rpc(MethodName.RpcSnapshot, data);

    public void Kick(long peerId, string reason)
    {
        RpcId(peerId, MethodName.RpcKicked, reason);
        // Give the reason a moment to arrive before cutting the connection.
        GetTree().CreateTimer(0.3).Timeout += () => _peer?.DisconnectPeer((int)peerId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RpcLobby(byte[] data) => LobbyReceived?.Invoke(data);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RpcMatchStart(int matchId) => MatchStartReceived?.Invoke(matchId);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RpcReturnToLobby() => ReturnToLobbyReceived?.Invoke();

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void RpcSnapshot(byte[] data) => SnapshotReceived?.Invoke(data);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RpcKicked(string reason) => _kickReason = reason;

    // ------------------------------------------------------------------ clients -> host

    public void SendHello(string name) => RpcId(1, MethodName.RpcHello, name, Protocol.Version);
    public void SendInput(float lateral) => RpcId(1, MethodName.RpcInput, lateral);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RpcHello(string name, int version)
    {
        if (IsHost) HelloReceived?.Invoke(Multiplayer.GetRemoteSenderId(), name, version);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void RpcInput(float lateral)
    {
        if (IsHost) InputReceived?.Invoke(Multiplayer.GetRemoteSenderId(), lateral);
    }
}
