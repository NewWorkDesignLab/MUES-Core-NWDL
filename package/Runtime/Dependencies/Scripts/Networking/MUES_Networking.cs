using Fusion;
using Meta.XR.BuildingBlocks;
using Meta.XR.MRUtilityKit;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using static Meta.XR.MultiplayerBlocks.Shared.CustomMatchmaking;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MUES_Networking : MonoBehaviour
{
    [Header("Networking Settings:")]
    [Tooltip("Anchor object placed at the center of the room for spatial anchoring.")]
    public GameObject roomMiddleAnchor;
    [Tooltip("Prefab for the session metadata object.")]
    public NetworkObject sessionMetaPrefab;
    [Tooltip("Prefab for the player marker object.")]
    public NetworkObject playerMarkerPrefab;
    [Tooltip("Prefab for the voice object.")]
    public NetworkObject voiceObjectPrefab;

    [Header("QR Code Transfer Settings:")]
    [Tooltip("URL for the QR code payload generation service.")]
    public string qrCodePayloadUrl;
    [Tooltip("URL to display the generated QR code for joining.")]
    public string qrCodeDisplayUrl;
    [Tooltip("Automatically open the QR code URL in the editor after room creation.")]
    public bool autoOpenInEditor = true;

    [Header("Room Settings:")]
    [Tooltip("Maximum number of players allowed in the room.")]
    [Range(2, 10)] public int maxPlayers = 10;
    [Tooltip("Enable to capture room dimensions on host join.")]
    public bool captureRoom = true;
    [Tooltip("If the avatars are shown for all users - even if they are not remote.")]
    public bool showAvatarsForColocated = true;
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode = false;

    [HideInInspector] public Guid anchorGroupUuid;  // The UUID for the shared spatial anchor group.
    [HideInInspector] public SharedSpatialAnchorCore spatialAnchorCore; // Reference to the shared spatial anchor core component.
    [HideInInspector] public bool isColocated, isConnected;  // Whether the player is colocated with the host based on IP.

    private NetworkRunner _runnerPrefab;    // Prefab for the NetworkRunner.
    private MRUK _mruk; // Reference to the MRUK component.           
    private string currentRoomToken;   // The token for the current room.

    public static MUES_Networking Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
            Instance = this;

        ImmersiveSceneDebugger debugger = FindFirstObjectByType<ImmersiveSceneDebugger>();

        if (debugger && isActiveAndEnabled)
        {
            debugger.gameObject.SetActive(false);
            ConsoleMessage.Send(debugMode, "Disabled ImmersiveSceneDebugger to prevent conflicts.", Color.yellow);
        }
    }

    private void Start()
    {
        _runnerPrefab = FindFirstObjectByType<NetworkRunner>();

        _mruk = FindFirstObjectByType<MRUK>();
        _mruk.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);

        spatialAnchorCore = FindFirstObjectByType<SharedSpatialAnchorCore>();
        spatialAnchorCore.OnAnchorCreateCompleted.AddListener(SaveAndShareAnchor);
        spatialAnchorCore.OnSharedSpatialAnchorsLoadCompleted.AddListener(OnSceneSetupCompleteAfterJoin);
    }

    private void OnDestroy()
    {
        _mruk.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
        spatialAnchorCore.OnAnchorCreateCompleted.RemoveListener(SaveAndShareAnchor);
        spatialAnchorCore.OnSharedSpatialAnchorsLoadCompleted.RemoveListener(OnSceneSetupCompleteAfterJoin);
    }

    #region Room Creation

    /// <summary>
    /// Creates a shared room and handles the result.
    /// </summary>
    public async void InitSharedRoom()
    {
        if(IsConnectedToRoom())
        {
            ConsoleMessage.Send(debugMode, "Already connected to a session, cannot create another.", Color.yellow);
            return;
        }

        MUES_LobbyControllerUI.Instance.ShowLoading(true);
        var loadResult = await LoadSceneWithTimeout(_mruk, 5f);

        if (loadResult == MRUK.LoadDeviceResult.Success)
        {
            ConsoleMessage.Send(debugMode, "Room geometry created - placing spatial anchor.", Color.green);

            Transform floorMiddle = GetRoomCenter();
            floorMiddle.GetComponentInChildren<Renderer>().enabled = captureRoom;

            spatialAnchorCore.InstantiateSpatialAnchor(roomMiddleAnchor, floorMiddle.position, floorMiddle.rotation);

            Transform roomTransform = FindFirstObjectByType<MRUKRoom>().transform;
            roomTransform.localScale = Vector3.zero;
        }
        else
        {
            ConsoleMessage.Send(debugMode, "Room scene loading failed or timed out. - Cannot create room.", Color.red);
            MUES_LobbyControllerUI.Instance.ShowLoading(false);
        }
    }

    /// <summary>
    /// Loads the scene from the MRUK device with a timeout.
    /// </summary>
    public async Task<MRUK.LoadDeviceResult> LoadSceneWithTimeout(MRUK mruk, float timeoutSeconds = 10f)
    {
        var loadTask = mruk.LoadSceneFromDevice();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

        var finished = await Task.WhenAny(loadTask, timeoutTask);

        if (finished == loadTask)
            return loadTask.Result;

        return MRUK.LoadDeviceResult.Failure;
    }

    /// <summary>
    /// Gets called when the scene setup is complete (shared spatial anchor is placed) and ready for room creation.
    /// </summary>
    public async void SaveAndShareAnchor(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult opResult)
    {
        if (opResult == OVRSpatialAnchor.OperationResult.Success)
        {
            ConsoleMessage.Send(debugMode, "Successfully placed spatial anchor - sharing anchor.", Color.green);

            anchorGroupUuid = Guid.NewGuid();

            var save = await anchor.SaveAnchorAsync();

            if (save.Success)
            {
                var share = await anchor.ShareAsync(anchorGroupUuid);

                if (share.Success)
                {
                    ConsoleMessage.Send(debugMode, "Anchor shared successfully. - Creating room.", Color.green);
                    CreateRoom();
                }
                else
                {
                    spatialAnchorCore.EraseAllAnchors();
                    MUES_LobbyControllerUI.Instance.ShowLoading(false);

                    ConsoleMessage.Send(debugMode, $"Anchor sharing failed: {share.Status}", Color.red);
                }
            }
            else
            {
                spatialAnchorCore.EraseAllAnchors();
                MUES_LobbyControllerUI.Instance.ShowLoading(false);

                ConsoleMessage.Send(debugMode, $"Anchor saving failed: {save.Status}", Color.red);
            }
        }
        else ConsoleMessage.Send(debugMode, $"Anchor spawning failed: {opResult}", Color.red);
    }

    /// <summary>
    /// Creates the room after the spatial anchor has been shared to the group.
    /// </summary>
    public async void CreateRoom()
    {
        ConsoleMessage.Send(debugMode, "Successfully shared spatial anchor - creating room.", Color.green);

        var result = await CreateSharedRoomWithToken();

        if (result.IsSuccess) OnRoomCreated(result);
        else
        {
            MUES_LobbyControllerUI.Instance.ShowLoading(false);
            ConsoleMessage.Send(debugMode, $"Room creation failed: {result.ErrorMessage}", Color.red);
        }
    }

    /// <summary>
    /// Task to create a shared room with a generated token.
    /// </summary>
    public async Task<RoomOperationResult> CreateSharedRoomWithToken()
    {
        var runner = InitializeNetworkRunner();
        var roomToken = RunTimeUtils.GenerateRandomString(6, false, true, false, false);
        ConsoleMessage.Send(debugMode, $"Trying to create room with token: {roomToken}", Color.cyan);

        var startArgs = new StartGameArgs
        {
            GameMode = GameMode.Shared,
            Scene = GetSceneInfo(),
            SessionName = roomToken,
            PlayerCount = maxPlayers, 
        };

        var result = await runner.StartGame(startArgs);

        return new RoomOperationResult
        {
            ErrorMessage = result.Ok ? null : $"Failed to Start: {result.ShutdownReason}, Error Message: {result.ErrorMessage}",
            RoomToken = roomToken,
            RoomPassword = null
        };
    }

    /// <summary>
    /// Gets called when a room is created.
    /// </summary>
    private void OnRoomCreated(RoomOperationResult result)
    {
        ConsoleMessage.Send(debugMode, $"Room created successfully with token: {result.RoomToken}.", Color.green);
        currentRoomToken = result.RoomToken;
    }

    /// <summary>
    /// Creates and sends the join QR code for the current room.
    /// </summary>
    public void EnableJoining()
    {
        MUES_SessionMeta.Instance.JoinEnabled = true;

        string qrPayload = $"MUESJoin_{currentRoomToken}";
        StartCoroutine(SendQrString(qrPayload)); 
    }

    /// <summary>
    /// Sends the QR string to the server to generate a QR code for joining the session.
    /// </summary>
    IEnumerator SendQrString(string qrPayload)
    {
        ConsoleMessage.Send(debugMode, $"QR payload: {qrPayload}", Color.cyan);

        WWWForm form = new WWWForm();
        form.AddField("data", qrPayload);

        using (UnityWebRequest www = UnityWebRequest.Post(qrCodePayloadUrl, form))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
#if UNITY_EDITOR
                if(autoOpenInEditor) Application.OpenURL(qrCodeDisplayUrl);
#endif
                ConsoleMessage.Send(debugMode, "QR-Request sent successfully.", Color.green);
            }
            else
            {
                LeaveRoom();
                Debug.LogError($"QR-Request failed: {www.result} - {www.error}");
            }
        }
    }

    #endregion

    #region Room Joining / Leaving

    /// <summary>
    /// When a trackable is added, check if it's a QR code and try to join the session.
    /// </summary>
    public void ScanQRCode(MRUKTrackable trackable)
    {
        if (trackable.TrackableType == OVRAnchor.TrackableType.QRCode &&
            trackable.MarkerPayloadString != null)
        {
            string content = trackable.MarkerPayloadString;
            ConsoleMessage.Send(debugMode, $"Detected QR code: {content}", Color.green);

            var parts = content.Split('_');

            if (parts.Length < 2 || parts[0] != "MUESJoin")
            {
                ConsoleMessage.Send(debugMode, "Detected QR Code - invalid QR-format for session joining.", Color.red);
                return;
            }

            string roomToken = parts[1];

            if (!IsConnectedToRoom()) JoinSessionFromCode(roomToken);
            else ConsoleMessage.Send(debugMode, "Already connected to a session, not joining another.", Color.yellow);
        }
    }

    /// <summary>
    /// Joins a session using the provided room token from the QR code.
    /// </summary>
    public async void JoinSessionFromCode(string roomToken)
    {
        MUES_LobbyControllerUI.Instance.ShowLoading(true);

        var result = await JoinRoomByToken(roomToken);

        if (!result.IsSuccess) Debug.LogError($"Room join failed: {result.ErrorMessage}");
        else ConsoleMessage.Send(debugMode, "Joined room via QR code.", Color.green);
    }

    /// <summary>
    /// Joins a room using the provided room token.
    /// </summary>
    public async Task<RoomOperationResult> JoinRoomByToken(string roomToken)
    {
        var runner = InitializeNetworkRunner();

        var startArgs = new StartGameArgs
        {
            GameMode = GameMode.Shared,
            Scene = GetSceneInfo(),
            SessionName = roomToken
        };

        var result = await runner.StartGame(startArgs);

        return new RoomOperationResult
        {
            ErrorMessage = result.Ok ? null : $"Failed to Start: {result.ShutdownReason}, Error Message: {result.ErrorMessage}",
            RoomToken = roomToken,
            RoomPassword = null
        };
    }

    /// <summary>
    /// Signals when the shared spatial anchors have been loaded after joining a room.
    /// </summary>
    private void OnSceneSetupCompleteAfterJoin(List<OVRSpatialAnchor> anchors, OVRSpatialAnchor.OperationResult result)
    {
        if (result != OVRSpatialAnchor.OperationResult.Success || anchors == null || anchors.Count == 0)
        {
            ConsoleMessage.Send(debugMode, $"Failed to load shared anchors: {result}", Color.red);
            LeaveRoom();
        }
    }

    /// <summary>
    /// Leaves the current room and shuts down all NetworkRunners.
    /// </summary>
    public void LeaveRoom()
    {
        for (int i = NetworkRunner.Instances.Count - 1; i >= 0; i--)
        {
            var runner = NetworkRunner.Instances[i];
            if (runner == null)
                continue;

            if (runner.IsRunning)
            {
                runner.Shutdown();
                Destroy(runner.gameObject);
            }
        }

        isConnected = false;
        ConsoleMessage.Send(debugMode, "Left room and shut down runners.", Color.yellow);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Initializes the NetworkRunner for the session.
    /// </summary>
    private NetworkRunner InitializeNetworkRunner()
    {
        _runnerPrefab.gameObject.SetActive(false);

        var runner = Instantiate(_runnerPrefab);
        runner.gameObject.SetActive(true);

        DontDestroyOnLoad(runner);
        runner.name = "Session Runner";

        return runner;
    }

    /// <summary>
    /// Gets the current scene info for the NetworkRunner.
    /// </summary>
    private static NetworkSceneInfo GetSceneInfo()
    {
        SceneRef sceneRef = default;
        if (TryGetActiveSceneRef(out var activeSceneRef)) sceneRef = activeSceneRef;

        var sceneInfo = new NetworkSceneInfo();
        if (sceneRef.IsValid) sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);

        return sceneInfo;
    }

    /// <summary>
    /// Fetches the active scene reference.
    /// </summary>
    private static bool TryGetActiveSceneRef(out SceneRef sceneRef)
    {
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.buildIndex < 0 || activeScene.buildIndex >= SceneManager.sceneCountInBuildSettings)
        {
            sceneRef = default;
            return false;
        }

        sceneRef = SceneRef.FromIndex(activeScene.buildIndex);
        return true;
    }

    /// <summary>
    /// Sets the session metadata with the provided anchor group UUID.
    /// </summary>
    public void SetSessionMeta(NetworkRunner runner)
    {
        MUES_SessionMeta _sessionMeta = MUES_SessionMeta.Instance;

        if (_sessionMeta == null && runner.IsSharedModeMasterClient)
        {
            var obj = runner.Spawn(sessionMetaPrefab, Vector3.zero, Quaternion.identity);
            _sessionMeta = obj.GetComponent<MUES_SessionMeta>();
        }

        if (_sessionMeta.Object != null && _sessionMeta.Object.HasStateAuthority)
        {
            _sessionMeta.AnchorGroup = anchorGroupUuid.ToString();
            _sessionMeta.HostIP = LocalIPAddress();

            ConsoleMessage.Send(debugMode, $"Session meta set: AnchorGroup={_sessionMeta.AnchorGroup}, HostIP={_sessionMeta.HostIP}", Color.cyan);
        }
        else ConsoleMessage.Send(debugMode, "Cannot set session meta - no state authority.", Color.red);
    }

    /// <summary>
    /// Returns the local IP address of the device.
    /// </summary>
    public string LocalIPAddress()
    {
        foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return ip.ToString();
        }
        return "0.0.0.0";
    }

    /// <summary>
    /// Checks if two IP addresses are in the same /24 network.
    /// </summary>
    public bool IsSameNetwork24(string ip1, string ip2)
    {
        if (string.IsNullOrWhiteSpace(ip1) || string.IsNullOrWhiteSpace(ip2))
            return false;

        var p1 = ip1.Split('.');
        var p2 = ip2.Split('.');

        if (p1.Length != 4 || p2.Length != 4)
            return false;

        return p1[0] == p2[0] && p1[1] == p2[1] && p1[2] == p2[2];
    }


    /// <summary>
    /// Gets executed when a trackable is added to the MRUK.
    /// </summary>
    void OnTrackableAdded(MRUKTrackable trackable)
    {
        ConsoleMessage.Send(debugMode, "Trackable added.", Color.green);
        ScanQRCode(trackable);
    }

    /// <summary>
    /// Returns whether the NetworkRunner is connected to a room.
    /// </summary>
    public bool IsConnectedToRoom()
    {
        foreach (var runner in NetworkRunner.Instances)
        {
            if (runner != null && runner.IsRunning)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Retrieves the transform of the Room Center Anchor in the scene.
    /// </summary>
    public static Transform GetRoomCenterAnchor()
    {
        var go = GameObject.FindWithTag("RoomCenterAnchor");
        return go ? go.transform : null;
    }

    /// <summary>
    /// Retrieves the transform of the room center (FLOOR object).
    /// </summary>
    /// <returns></returns>
    public static Transform GetRoomCenter()
    {
        var go = GameObject.Find("FLOOR").transform;
        return go ? go.transform : null;
    }

    /// <summary>
    /// Configures the main camera based on whether Insight Passthrough is enabled.
    /// </summary>
    public void ConfigureCamera()
    {
        OVRManager manager = OVRManager.instance;
        if (manager == null) return;

        manager.isInsightPassthroughEnabled = isColocated;
        Camera main = Camera.main;

        main.allowHDR = !manager.isInsightPassthroughEnabled;
        main.clearFlags = manager.isInsightPassthroughEnabled ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
    }

    /// <summary>
    /// Spawns the avatar marker for the given player.
    /// </summary>
    public void SpawnAvatarMarker(NetworkRunner runner, PlayerRef player)
    {
        var marker = runner.Spawn(playerMarkerPrefab, Vector3.zero, Quaternion.identity);

        marker.RequestStateAuthority();
        marker.AssignInputAuthority(player);
        runner.SetPlayerObject(player, marker);

        isConnected = true;
        ConsoleMessage.Send(debugMode, $"Local player {player} spawned marker. StateAuthority={marker.StateAuthority}", Color.cyan);
    }

    #endregion 
}

[CustomEditor(typeof(MUES_Networking))]
public class MUES_NetworkingEditor : Editor
{
    private string joinToken = "";

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MUES_Networking networking = (MUES_Networking)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Matchmaking Controls:", EditorStyles.boldLabel);

        joinToken = EditorGUILayout.TextField("Debug Room Token", joinToken);
        EditorGUILayout.Space();

        if (GUILayout.Button("Create room (Host)"))
        {
            if (Application.isPlaying) networking.InitSharedRoom();
        }

        if (GUILayout.Button("Join room (Client)"))
        {
            if (Application.isPlaying)
            {
                if (!string.IsNullOrEmpty(joinToken)) networking.JoinSessionFromCode(joinToken);
                else ConsoleMessage.Send(networking.debugMode, "Didn't set join token!.", Color.yellow);
            }
        }

        if (GUILayout.Button("Leave room"))
        {
            if (Application.isPlaying) networking.LeaveRoom();
        }
    }
}
