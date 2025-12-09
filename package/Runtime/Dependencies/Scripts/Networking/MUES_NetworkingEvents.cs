using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MUES_NetworkingEvents : MonoBehaviour, INetworkRunnerCallbacks
{
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode = false;

    #region General

    /// <summary>
    /// Gets called when a player joins the session.
    /// </summary>
    public virtual void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        ConsoleMessage.Send(debugMode, $"Player {player} joined.", Color.green);
        if (player != runner.LocalPlayer) return;

        var net = MUES_Networking.Instance;

        if (!runner.IsSharedModeMasterClient) StartCoroutine(HandleNonHostJoin(net, runner, player));
        else
        {
            net.isColocated = true;

            net.SetSessionMeta(runner);
            net.ConfigureCamera();
            net.SpawnAvatarMarker(runner, player);

            if (net.captureRoom)
            {
                if(string.IsNullOrEmpty(MUES_RoomVisualizer.Instance.roomDataPath)) MUES_RoomVisualizer.Instance.CaptureRoom();
                else MUES_RoomVisualizer.Instance.LoadRoomDataFromFile(MUES_RoomVisualizer.Instance.roomDataPath);
            }
        }
    }

    /// <summary>
    /// Handles the joining process for non-host players.
    /// </summary>
    private IEnumerator HandleNonHostJoin(MUES_Networking net, NetworkRunner runner, PlayerRef player)
    {
        while (MUES_SessionMeta.Instance == null)
            yield return null;

        var meta = MUES_SessionMeta.Instance;

        while (!meta.JoinEnabled)
        {
            ConsoleMessage.Send(debugMode, "Waiting for host to enable joining...", Color.yellow);
            yield return null;
        }

        if (string.IsNullOrEmpty(meta.AnchorGroup.ToString()) || string.IsNullOrEmpty(meta.HostIP.ToString()))
        {
            ConsoleMessage.Send(debugMode, "Session Meta data is incomplete. Cannot load shared anchors.", Color.red);
            yield break;
        }

        if (Guid.TryParse(meta.AnchorGroup.ToString(), out Guid id) && id != Guid.Empty) net.anchorGroupUuid = id;
        else ConsoleMessage.Send(true, $"Invalid or empty AnchorGroup '{meta.AnchorGroup}' (len={meta.AnchorGroup.Length})", Color.red);

        net.isColocated = net.IsSameNetwork24(meta.HostIP.ToString(), net.LocalIPAddress());

        ConsoleMessage.Send(debugMode, $"Session Meta found. AnchorGroup: {meta.AnchorGroup}, HostIP: {meta.HostIP}, LocalIP: {net.LocalIPAddress()}, isColocated: {net.isColocated}", Color.cyan);

        if (net.anchorGroupUuid != Guid.Empty)
        {
            net.spatialAnchorCore.LoadAndInstantiateAnchorsFromGroup(net.roomMiddleAnchor, net.anchorGroupUuid);
            ConsoleMessage.Send(debugMode, "Waiting to load shared anchors for group: " + net.anchorGroupUuid, Color.cyan);
        }
        else ConsoleMessage.Send(debugMode, "No valid anchorGroupUuid set yet – cannot load shared anchors.", Color.red);

        net.ConfigureCamera();

        if (!net.isColocated)
        {
            ConsoleMessage.Send(debugMode, "Different networks detected between host and client. - Loading cached room geometry for remote user.", Color.cyan);

            var roomVis = MUES_RoomVisualizer.Instance;
            if (roomVis != null && roomVis.HasRoomData && !net.isColocated) roomVis.SendRoomDataTo(player);
        }

        net.SpawnAvatarMarker(runner, player);
    }

    /// <summary>
    /// Gets called when a player leaves the session.
    /// </summary>
    public virtual void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (runner.SessionInfo.PlayerCount == 1)
        {
            ConsoleMessage.Send(debugMode, "All players left the session.", Color.red);
            MUES_Networking.Instance.spatialAnchorCore.EraseAllAnchors();
        }

        if (runner.IsSharedModeMasterClient) MUES_SessionMeta.Instance.HostIP = MUES_Networking.Instance.LocalIPAddress();

        ConsoleMessage.Send(debugMode, $"Player {player} left.", Color.yellow);
    }

    #endregion

    #region Other INetworkRunnerCallbacks Methods

    public virtual void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public virtual void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public virtual void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
    public virtual void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public virtual void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public virtual void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public virtual void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public virtual void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public virtual void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public virtual void OnInput(NetworkRunner runner, NetworkInput input) { }
    public virtual void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public virtual void OnConnectedToServer(NetworkRunner runner) { }
    public virtual void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public virtual void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public virtual void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public virtual void OnSceneLoadDone(NetworkRunner runner) { }
    public virtual void OnSceneLoadStart(NetworkRunner runner) { }

    #endregion
}