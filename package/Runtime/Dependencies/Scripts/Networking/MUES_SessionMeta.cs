using Fusion;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Serializable player info for the session.
/// </summary>
[System.Serializable]
public struct PlayerInfo : INetworkStruct
{
    public PlayerRef PlayerRef;
    public NetworkString<_64> PlayerName;
}

public class MUES_SessionMeta : NetworkBehaviour
{
    [Tooltip("A unique identifier for this session used to group anchors.")]
    [Networked] public NetworkString<_64> AnchorGroup { get; set; }

    [Tooltip("The IP address of the host player.")]
    [Networked] public NetworkString<_32> HostIP { get; set; }

    [Tooltip("Whether new players are allowed to join the session.")]
    [Networked] public NetworkBool JoinEnabled { get; set; }

    [Tooltip("The host's scene parent world position for anchor synchronization.")]
    [Networked] public Vector3 HostSceneParentPosition { get; set; }

    [Tooltip("The host's scene parent world rotation for anchor synchronization.")]
    [Networked] public Quaternion HostSceneParentRotation { get; set; }

    [HideInInspector][Networked, Capacity(4096)] public NetworkArray<byte> RoomDataBlob { get; }    // Compressed RoomData storage

    [HideInInspector][Networked, Capacity(10)] public NetworkLinkedList<PlayerInfo> ConnectedPlayers => default;    // List of connected players in the session

    private const int MAX_ROOM_DATA_SIZE = 4096;    // Maximum size for RoomData in bytes

    public static MUES_SessionMeta Instance { get; private set; }   // Singleton instance

    public override void Spawned() => Instance = this;

    #region Anchor Synchronization

    /// <summary>
    /// Updates the host's scene parent position for client synchronization.
    /// </summary>
    public void UpdateHostSceneParentPose(Vector3 position, Quaternion rotation)
    {
        if (!Object.HasStateAuthority) return;
        
        HostSceneParentPosition = position;
        HostSceneParentRotation = rotation;
        
        ConsoleMessage.Send(true, $"[SessionMeta] Updated host scene parent pose: pos={position}, rot={rotation.eulerAngles}", Color.cyan);
    }

    /// <summary>
    /// Requests all anchored objects to resync their offsets from the current sceneParent.
    /// </summary>
    public void RequestAnchorResync()
    {
        if (Object.HasStateAuthority)
            PerformAnchorResync();
        else
            RPC_RequestAnchorResync();
    }

    /// <summary>
    /// Requests the host to perform an anchor resync.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestAnchorResync() => PerformAnchorResync();

    /// <summary>
    /// Forces all anchored network objects to recalculate their offsets based on current world positions.
    /// </summary>
    private void PerformAnchorResync()
    {
        var net = MUES_Networking.Instance;
        if (net == null || net.sceneParent == null)
        {
            ConsoleMessage.Send(true, "[SessionMeta] Cannot resync anchors - no sceneParent available.", Color.yellow);
            return;
        }

        UpdateHostSceneParentPose(net.sceneParent.position, net.sceneParent.rotation);

        var anchoredObjects = FindObjectsByType<MUES_AnchoredNetworkBehaviour>(FindObjectsSortMode.None);
        int syncCount = 0;

        foreach (var obj in anchoredObjects)
        {
            if (obj == null || obj.Object == null || !obj.Object.IsValid) continue;
            
            if (obj.Object.HasStateAuthority || obj.Object.HasInputAuthority)
            {
                obj.ForceUpdateAnchorOffset();
                syncCount++;
            }
        }

        ConsoleMessage.Send(true, $"[SessionMeta] Anchor resync completed. Synced {syncCount} objects.", Color.green);
    }

    #endregion

    #region Model Spawn Requests

    /// <summary>
    /// Requests the host to spawn a model. Called by non-master clients.
    /// </summary>
    public void RequestSpawnModel(string modelFileName, bool makeGrabbable, bool spawnerGrabOnly, PlayerRef requestingPlayer, Vector3 anchorRelativePosition, Quaternion anchorRelativeRotation)
    {
        RPC_RequestSpawnModel(modelFileName, makeGrabbable, spawnerGrabOnly, requestingPlayer, anchorRelativePosition, anchorRelativeRotation);
    }

    /// <summary>
    /// RPC called by non-master clients to request the host to spawn a model.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnModel(string modelFileName, NetworkBool makeGrabbable, NetworkBool spawnerGrabOnly, PlayerRef requestingPlayer, Vector3 anchorRelativePosition, Quaternion anchorRelativeRotation)
    {
        Vector3 worldSpawnPos = anchorRelativePosition;
        Quaternion worldSpawnRot = anchorRelativeRotation;

        var sceneParent = MUES_Networking.Instance?.sceneParent;
        if (sceneParent != null)
        {
            worldSpawnPos = sceneParent.TransformPoint(anchorRelativePosition);
            worldSpawnRot = sceneParent.rotation * anchorRelativeRotation;
            ConsoleMessage.Send(true, $"[SessionMeta] Converted RPC spawn: pos {anchorRelativePosition} -> {worldSpawnPos}, rot {anchorRelativeRotation.eulerAngles} -> {worldSpawnRot.eulerAngles}", Color.cyan);
        }
        else
            ConsoleMessage.Send(true, "[SessionMeta] Warning: No sceneParent found on Host. Spawning at received local pos (might be wrong).", Color.yellow);

        ConsoleMessage.Send(true, $"[SessionMeta] Host received spawn request from {requestingPlayer} for: {modelFileName} at {worldSpawnPos}", Color.cyan);
        MUES_NetworkedObjectManager.Instance?.SpawnModelContainer(modelFileName, makeGrabbable, spawnerGrabOnly, requestingPlayer, worldSpawnPos, worldSpawnRot);
    }

    #endregion

    #region Player Management

    /// <summary>
    /// Registers a player in the session's connected players list.
    /// </summary>
    public void RegisterPlayer(PlayerRef playerRef, string playerName)
    {
        if (!Object.HasStateAuthority)
        {
            RPC_RequestRegisterPlayer(playerRef, playerName);
            return;
        }

        if (ConnectedPlayers.Any(p => p.PlayerRef == playerRef)) return;

        ConnectedPlayers.Add(new PlayerInfo
        {
            PlayerRef = playerRef,
            PlayerName = playerName,
        });
        ConsoleMessage.Send(true, $"[SessionMeta] Player registered: {playerName} (Ref: {playerRef})", Color.green);
    }

    /// <summary>
    /// RPC to request player registration from clients without state authority.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRegisterPlayer(PlayerRef playerRef, string playerName) => RegisterPlayer(playerRef, playerName);

    /// <summary>
    /// Unregisters a player from the session's connected players list.
    /// </summary>
    public void UnregisterPlayer(PlayerRef playerRef)
    {
        if (this == null || Object == null || !Object.IsValid) return;
        
        if (!Object.HasStateAuthority)
        {
            try
            {
                RPC_RequestUnregisterPlayer(playerRef);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SessionMeta] Failed to request unregister: {ex.Message}");
            }
            return;
        }

        try
        {
            var playerToRemove = ConnectedPlayers.FirstOrDefault(p => p.PlayerRef == playerRef);
            if (playerToRemove.PlayerRef == playerRef)
            {
                string name = playerToRemove.PlayerName.ToString();
                ConnectedPlayers.Remove(playerToRemove);
                ConsoleMessage.Send(true, $"[SessionMeta] Player unregistered: {name} (Ref: {playerRef})", Color.yellow);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[SessionMeta] Error during unregister: {ex.Message}");
        }
    }

    /// <summary>
    /// RPC to request player unregistration from clients without state authority.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestUnregisterPlayer(PlayerRef playerRef) => UnregisterPlayer(playerRef);

    /// <summary>
    /// Gets all connected players as a list.
    /// </summary>
    public List<PlayerInfo> GetAllPlayers() => ConnectedPlayers.ToList();

    /// <summary>
    /// Gets a player by their PlayerRef.
    /// </summary>
    public PlayerInfo? GetPlayerByRef(PlayerRef playerRef)
    {
        foreach (var player in ConnectedPlayers)
            if (player.PlayerRef == playerRef)
                return player;

        return null;
    }

    #endregion

    #region Data Compression Helpers

    /// <summary>
    /// Serializes, compresses, and stores the RoomData into the NetworkArray.
    /// </summary>
    public void SetRoomData(RoomData data)
    {
        if (!Object.HasStateAuthority) return;

        string json = JsonUtility.ToJson(data);
        byte[] compressed = Compress(json);

        if (compressed.Length > MAX_ROOM_DATA_SIZE)
        {
            Debug.LogError($"[MUES_SessionMeta] Room Data is too large! Size: {compressed.Length} bytes. Max: {MAX_ROOM_DATA_SIZE} bytes.");
            return;
        }

        RoomDataBlob.CopyFrom(compressed, 0, compressed.Length);
        ConsoleMessage.Send(true, $"[MUES_SessionMeta] RoomData stored. Compressed size: {compressed.Length} bytes.", Color.green);
    }

    /// <summary>
    /// Reads, decompresses, and deserializes RoomData from the NetworkArray.
    /// </summary>
    public RoomData GetRoomData()
    {
        if (RoomDataBlob.Length == 0) return null;

        byte[] data = RoomDataBlob.ToArray();
        if (data[0] == 0 && data[1] == 0) return null;

        try
        {
            string json = Decompress(data);
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<RoomData>(json);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MUES_SessionMeta] Error reading room data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Compresses a string into a byte array.
    /// </summary>
    private static byte[] Compress(string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        using (var mso = new MemoryStream())
        {
            using (var gs = new GZipStream(mso, CompressionMode.Compress))
                gs.Write(bytes, 0, bytes.Length);
            return mso.ToArray();
        }
    }

    /// <summary>
    /// Decompresses a byte array back into a string.
    /// </summary>
    private static string Decompress(byte[] bytes)
    {
        using (var msi = new MemoryStream(bytes))
        using (var mso = new MemoryStream())
        {
            using (var gs = new GZipStream(msi, CompressionMode.Decompress))
            {
                try { gs.CopyTo(mso); } catch { Debug.LogError("End of Stream."); }
            }
            return Encoding.UTF8.GetString(mso.ToArray());
        }
    }

    #endregion
}

