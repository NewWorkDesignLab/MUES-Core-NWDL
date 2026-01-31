using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;

public class MUES_NetworkingEvents : MonoBehaviour, INetworkRunnerCallbacks
{
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode = false;

    private static MUES_NetworkingEvents _instance;
    public static MUES_NetworkingEvents Instance => _instance;

    private void Awake()
    {
        if (_instance == null)
            _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    /// <summary>
    /// Registers this callback handler with the given NetworkRunner.
    /// Call this from MUES_Networking when initializing the runner.
    /// </summary>
    public void RegisterWithRunner(NetworkRunner runner)
    {
        if (runner != null)
        {
            runner.AddCallbacks(this);
            ConsoleMessage.Send(debugMode, "Registered NetworkingEvents callbacks with runner.", Color.cyan);
        }
    }

    /// <summary>
    /// Unregisters this callback handler from the given NetworkRunner.
    /// </summary>
    public void UnregisterFromRunner(NetworkRunner runner)
    {
        if (runner != null)
        {
            runner.RemoveCallbacks(this);
            ConsoleMessage.Send(debugMode, "Unregistered NetworkingEvents callbacks from runner.", Color.cyan);
        }
    }

    #region General

    /// <summary>
    /// Gets called when a player joins the session.
    /// </summary>
    public virtual void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        ConsoleMessage.Send(debugMode, $"Player {player} joined.", Color.green);

        var net = MUES_Networking.Instance;

        if (runner.IsSharedModeMasterClient) net._previousMasterClient = runner.LocalPlayer;  
        
        // Handle local player joining
        if (player == runner.LocalPlayer)
        {
            if (runner.IsSharedModeMasterClient)
            {
                if (net.isJoiningAsClient)
                {
                    ConsoleMessage.Send(debugMode, "ERROR: Joined as Master but expected Client (Room not found?). Leaving.", Color.red);
                    net.LeaveRoom();
                    return;
                }
            
                net.HandleHostJoin(player);
            }
            else net.StartCoroutine(net.HandleNonHostJoin(player));
        }
        else net.InvokeOnPlayerJoined(player);
    }

    /// <summary>
    /// Gets called when a player leaves the session.
    /// </summary>
    public virtual void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        ConsoleMessage.Send(debugMode, $"Player {player} left.", Color.yellow);
        
        if (MUES_SessionMeta.Instance != null && MUES_SessionMeta.Instance.Object != null && MUES_SessionMeta.Instance.Object.IsValid)
            MUES_SessionMeta.Instance.UnregisterPlayer(player);
        
        MUES_Networking.Instance.CheckIfNewMaster(player);
    }   

    /// <summary>
    /// Gets called when the runner shuts down.
    /// </summary>
    public virtual void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        var net = MUES_Networking.Instance;

        ConsoleMessage.Send(debugMode, "Network Runner Shut Down -- Cleaning up.", Color.yellow);

        if (net == null) return;

        net.spatialAnchorCore?.EraseAllAnchors();
            
        if (net.isRemote)
            MUES_RoomVisualizer.Instance?.ClearRoomVisualization();
    }

    #endregion

    #region Other INetworkRunnerCallbacks Methods

    public virtual void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public virtual void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
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