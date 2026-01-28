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
using static OVRInput;
using Meta.XR;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MUES_Networking : MonoBehaviour
{
    [Header("References:")]
    [Tooltip("Anchor object placed at the center of the room for spatial anchoring.")]
    public GameObject roomMiddleAnchor;
    [Tooltip("Prefab for the session metadata object.")]
    public NetworkObject sessionMetaPrefab;
    [Tooltip("Prefab for the player marker object.")]
    public NetworkObject playerMarkerPrefab;

    [Header("Room Settings:")]
    [Tooltip("Maximum number of players allowed in the room.")]
    [Range(2, 10)] public int maxPlayers = 10;

    [Header("Debug Settings:")]
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode = false;
    [Tooltip("If the QR Code for joining gets displayed immediately.")]
    public bool popUpQRCode = false;
    [Tooltip("If the avatars are shown for all users - even if they are not remote.")]
    public bool showAvatarsForColocated = true;
    [Tooltip("Forces the client into remote mode (Debug only)")]
    public bool forceClientRemote = false;

    [HideInInspector] public Guid anchorGroupUuid;  // The UUID for the shared spatial anchor group.
    [HideInInspector] public SharedSpatialAnchorCore spatialAnchorCore; // Reference to the shared spatial anchor core component.
    [HideInInspector] public Transform anchorTransform, sceneParent; // Parent transform for instantiated scene objects.
    [HideInInspector] public GameObject loadingText;    // Loading text object.
    [HideInInspector] public bool isRemote, isConnected, isJoiningAsClient;  // Networking state flags.
    [HideInInspector] public float heightCalibrationOffset = 0f; // Y-offset calculated from local floor vs host floor
    [HideInInspector] public PlayerRef _previousMasterClient = PlayerRef.None; // Previous master client reference.

    private Vector3 _sceneVelocity; // Velocity for scene smoothing.
    private NetworkRunner _runnerPrefab;    // Prefab for the NetworkRunner.
    private MRUK _mruk; // Reference to the MRUK component.
    private EnvironmentRaycastManager raycastManager; // Reference to the environment raycast manager.
    private SpriteRenderer depthIndex;  // Depth index sprite renderer.

    private Camera _mainCam;
    private Camera mainCam => _mainCam != null ? _mainCam : (_mainCam = Camera.main);

    private string currentRoomToken;   // The token for the current room.
    private bool isCreatingRoom, isInitalizingRoomCreation; // Room creation state flags.
    private EstimatedRoomCreationQuality estimatedRoomQuality = EstimatedRoomCreationQuality.Poor; // Estimated quality of the room creation.

    private float maxDistanceThreshold = 0.5f;  // Maximum distance in meters to consider the anchor valid.
    private float glitchTimeThreshold = 0.5f;   // Time in seconds to consider a glitch valid.
    private float _glitchTimer;    // Timer for tracking how long the anchor has been out of bounds.
    private Vector3 _lastValidAnchorPos;    // Last valid position of the anchor.
    private Quaternion _lastValidAnchorRot; // Last valid rotation of the anchor.
    private bool _isFirstFrame = true;  // Flag to check if it's the first frame of update.

    private const float sceneParentPositionSmoothing = 0.5f; // Smoothing factor for scene parent position.
    private const float sceneParentRotationSmoothing = 2f;   // Smoothing factor for scene parent rotation.

    public static MUES_Networking Instance { get; private set; }

    public enum EstimatedRoomCreationQuality
    {
        Poor,
        Average,
        Good,
    }

    /// <summary>
    /// Gets the first active and running NetworkRunner instance.
    /// </summary>
    public NetworkRunner Runner
    {
        get
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return runner;
            }
            return null;
        }
    }

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

        raycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();

#if UNITY_EDITOR
        raycastManager.gameObject.SetActive(false);
        ConsoleMessage.Send(debugMode, "Disabled EnvironmentRaycastManager in Editor to prevent conflicts.", Color.yellow);
#endif
    }

    private void Start()
    {
        _runnerPrefab = FindFirstObjectByType<NetworkRunner>();
        _mruk = FindFirstObjectByType<MRUK>();
        _mruk.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);

        spatialAnchorCore = FindFirstObjectByType<SharedSpatialAnchorCore>();
        spatialAnchorCore.OnSharedSpatialAnchorsLoadCompleted.AddListener(OnSceneSetupCompleteAfterJoin);
        spatialAnchorCore.OnAnchorCreateCompleted.AddListener(SaveAndShareAnchor);

        loadingText = GameObject.Find("LoadingText");
        loadingText.SetActive(false);

        depthIndex = FindFirstObjectByType<SpriteRenderer>();
    }

    private void Update()
    {
        ConfirmRoomCreation();
        depthIndex.gameObject.SetActive(isInitalizingRoomCreation);
        UpdateSceneParent();

        if (!loadingText.activeSelf && !depthIndex.gameObject.activeSelf) return;

        Vector3 camForwardFlat = Vector3.ProjectOnPlane(mainCam.transform.forward, Vector3.up).normalized;
        Vector3 camPos = mainCam.transform.position;

        if (loadingText.activeSelf) 
            loadingText.transform.position = camPos + camForwardFlat * 0.5f;
        if (depthIndex.gameObject.activeSelf) 
            depthIndex.transform.position = camPos + camForwardFlat * 0.7f;
    }

    void OnEnable() => OVRManager.HMDMounted += () => StartCoroutine(ShowLoadingOnHMDMounted());

    void OnDisable() => OVRManager.HMDMounted -= () => StartCoroutine(ShowLoadingOnHMDMounted());

    private void OnDestroy()
    {
        _mruk.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
        spatialAnchorCore.OnSharedSpatialAnchorsLoadCompleted.RemoveListener(OnSceneSetupCompleteAfterJoin);
        spatialAnchorCore.OnAnchorCreateCompleted.RemoveListener(SaveAndShareAnchor);
    }

    #region Host - Room Creation

    /// <summary>
    /// Initiates the room creation process.
    /// </summary>
    public void StartLobbyCreation()
    {
        if (IsConnectedToRoom())
        {
            ConsoleMessage.Send(debugMode, "Already connected to a session, cannot create another.", Color.yellow);
            return;
        }

        isCreatingRoom = isInitalizingRoomCreation = true;
        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(true);
        MUES_RoomVisualizer.Instance.RenderRoomGeometry(false);
    }

    /// <summary>
    /// Confirms the room creation based on user input and estimated room quality.
    /// </summary>
    private void ConfirmRoomCreation()
    {
        if (!isInitalizingRoomCreation) return;

#if !UNITY_EDITOR
        Ray camForward = new(mainCam.transform.position, mainCam.transform.forward);
        raycastManager.Raycast(camForward, out EnvironmentRaycastHit hit, 30);

        float distanceToHit = Vector3.Distance(mainCam.transform.position, hit.point);

        switch (distanceToHit)
        {
            case >= 4f:
                estimatedRoomQuality = EstimatedRoomCreationQuality.Good;
                depthIndex.color = Color.green;
                break;
            case >= 2f:
                estimatedRoomQuality = EstimatedRoomCreationQuality.Average;
                depthIndex.color = Color.yellow;
                break;
            default:
                estimatedRoomQuality = EstimatedRoomCreationQuality.Poor;
                depthIndex.color = Color.red;
                break;
        }

        if (estimatedRoomQuality == EstimatedRoomCreationQuality.Poor)
        {
            ConsoleMessage.Send(debugMode, "Room creation aborted - Room quality poor.", Color.red);
            return;
        }

        if (!GetDown(RawButton.RIndexTrigger, Controller.RTouch)) return;

        ConsoleMessage.Send(debugMode, "Room creation confirmed by user input.", Color.green);
        isInitalizingRoomCreation = false;
        InitSharedRoom();
#else
        ConsoleMessage.Send(debugMode, "UnityEditor - Skipping depth raycasting - Room scanning quality may be lower.", Color.yellow);
        isInitalizingRoomCreation = false;
        InitSharedRoom();
#endif
    }

    /// <summary>
    /// Creates a shared room and handles the result.
    /// </summary>
    public async void InitSharedRoom()
    {
        var loadResult = await LoadSceneWithTimeout(_mruk, 5f);

        if (loadResult != MRUK.LoadDeviceResult.Success)
        {
            AbortLobbyCreation();
            ConsoleMessage.Send(debugMode, "Room scene loading failed or timed out. - Do you have a scanned room on your headset?", Color.red);
            return;
        }

        ConsoleMessage.Send(debugMode, "Room geometry created - placing spatial anchor.", Color.green);

        GameObject.Find("CEILING_EffectMesh").layer = GameObject.Find("FLOOR_EffectMesh").layer = 11;

        MRUKRoom room = _mruk.GetCurrentRoom();
        OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();

        var floorAnchors = room.FloorAnchors;
        if (floorAnchors == null || floorAnchors.Count == 0)
        {
            AbortLobbyCreation();
            ConsoleMessage.Send(debugMode, "No floor anchors found in the room.", Color.red);
            return;
        }

        var primaryFloor = floorAnchors[0];
        Vector3 floorPos = new(primaryFloor.transform.position.x, rig.trackingSpace.position.y, primaryFloor.transform.position.z);
        room.transform.position = floorPos;

        Quaternion flatRotation = Quaternion.Euler(0f, primaryFloor.transform.rotation.eulerAngles.y, 0f);
        spatialAnchorCore.InstantiateSpatialAnchor(roomMiddleAnchor, primaryFloor.transform.position, flatRotation);
    }

    /// <summary>
    /// Loads the scene from the MRUK device with a timeout.
    /// </summary>
    public async Task<MRUK.LoadDeviceResult> LoadSceneWithTimeout(MRUK mruk, float timeoutSeconds = 10f)
    {
        var loadTask = mruk.LoadSceneFromDevice();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

        var finished = await Task.WhenAny(loadTask, timeoutTask);
        return finished == loadTask ? loadTask.Result : MRUK.LoadDeviceResult.Failure;
    }

    /// <summary>
    /// Gets called when the scene setup is complete (shared spatial anchor is placed) and ready for room creation.
    /// </summary>
    public async void SaveAndShareAnchor(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult opResult)
    {
        if (opResult != OVRSpatialAnchor.OperationResult.Success)
        {
            ConsoleMessage.Send(debugMode, $"Anchor spawning failed: {opResult}", Color.red);
            return;
        }

        ConsoleMessage.Send(debugMode, "Successfully placed spatial anchor - sharing anchor.", Color.green);

        anchorGroupUuid = Guid.NewGuid();
        var save = await anchor.SaveAnchorAsync();

        if (!save.Success)
        {
            ConsoleMessage.Send(debugMode, $"Anchor saving failed: {save.Status}", Color.red);
            AbortLobbyCreation();
            return;
        }

        InitSceneParent();
        ConsoleMessage.Send(debugMode, $"Anchor transform set directly: {anchorTransform.name} at {anchorTransform.position}", Color.green);

        var share = await anchor.ShareAsync(anchorGroupUuid);

        if (share.Success)
        {
            ConsoleMessage.Send(debugMode, "Anchor shared successfully. - Creating room.", Color.green);
            CreateRoom();
        }
        else
        {
            ConsoleMessage.Send(debugMode, $"Anchor sharing failed: {share.Status} - Retrying", Color.red);
            _mruk.ClearScene();
            spatialAnchorCore.EraseAllAnchors();
            await Task.Delay(500);
            InitSharedRoom();
        }
    }

    /// <summary>
    /// Initializes the scene parent transform for instantiated objects.
    /// </summary>
    public void InitSceneParent()
    {
        if (anchorTransform == null)
        {
            var anchorGO = GameObject.FindWithTag("RoomCenterAnchor");
            if (anchorGO != null)
            {
                anchorTransform = anchorGO.transform;
                ConsoleMessage.Send(debugMode, $"InitSceneParent: Found anchor via tag: {anchorTransform.name} at {anchorTransform.position}", Color.cyan);
            }
        }

        sceneParent = GetOrCreateSceneParent();
        ConsoleMessage.Send(debugMode, sceneParent.gameObject.scene.IsValid() ? "InitSceneParent: Found existing SCENE_PARENT" : "InitSceneParent: Created new SCENE_PARENT", Color.cyan);

        if (anchorTransform == null)
        {
            ConsoleMessage.Send(debugMode, "InitSceneParent: anchorTransform=NULL", Color.cyan);
            return;
        }

        var (anchorPos, flatRotation) = GetFloorAlignedPose(anchorTransform);

        sceneParent.SetPositionAndRotation(anchorPos, flatRotation);
        _lastValidAnchorPos = anchorPos;
        _lastValidAnchorRot = flatRotation;
        _isFirstFrame = false;

        ConsoleMessage.Send(debugMode, $"InitSceneParent: Synced SCENE_PARENT to anchor at {anchorPos}", Color.green);

        if (MUES_SessionMeta.Instance != null && MUES_SessionMeta.Instance.Object != null && MUES_SessionMeta.Instance.Object.HasStateAuthority)
            MUES_SessionMeta.Instance.UpdateHostSceneParentPose(anchorPos, flatRotation);

        ConsoleMessage.Send(debugMode, $"InitSceneParent: anchorTransform={anchorTransform.name}", Color.cyan);
    }

    /// <summary>
    /// Erases all spatial anchors as well as the scene in case of failure.
    /// </summary>
    private void AbortLobbyCreation()
    {
        _mruk.ClearScene();
        spatialAnchorCore.EraseAllAnchors();
        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
        isCreatingRoom = false;
    }

    /// <summary>
    /// Creates the room after the spatial anchor has been shared to the group.
    /// </summary>
    public async void CreateRoom()
    {
        var result = await CreateSharedRoomWithToken();

        if (result.IsSuccess)
            OnRoomCreated(result);
        else
        {
            ConsoleMessage.Send(debugMode, $"Room creation failed: {result.ErrorMessage}", Color.red);
            AbortLobbyCreation();
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
        isCreatingRoom = false;
    }

    /// <summary>
    /// Enables joining for other players by sending the QR code to the server and updating the session meta.
    /// </summary>
    public void EnableJoining()
    {
        if (MUES_SessionMeta.Instance != null && MUES_SessionMeta.Instance.Object.HasStateAuthority)
        {
            var roomVis = MUES_RoomVisualizer.Instance;

            if (roomVis != null && roomVis.HasRoomData)
                MUES_SessionMeta.Instance.SetRoomData(roomVis.GetCurrentRoomData());
        }

        MUES_SessionMeta.Instance.JoinEnabled = true;
        isConnected = true;

        StartCoroutine(SendQrString($"MUESJoin_{currentRoomToken}"));
    }

    /// <summary>
    /// Sends the QR string to the server to generate a QR code for joining the session.
    /// </summary>
    IEnumerator SendQrString(string qrPayload)
    {
        ConsoleMessage.Send(debugMode, $"QR payload: {qrPayload}", Color.cyan);

        WWWForm form = new();
        form.AddField("token", "YOUR_TOKEN");
        form.AddField("data", qrPayload);

        using UnityWebRequest www = UnityWebRequest.Post("YOUR_DOMAIN/mues/setqr.php", form);
        yield return www.SendWebRequest();

        if (www.result == UnityWebRequest.Result.Success)
        {
#if UNITY_EDITOR
            if (popUpQRCode) Application.OpenURL("YOUR_DOMAIN");
#endif
            ConsoleMessage.Send(debugMode, "QR-Request sent successfully.", Color.green);
        }
        else
            ConsoleMessage.Send(debugMode, $"QR-Request failed: {www.result} - {www.error}", Color.red);
    }

    #endregion

    #region Joining - Host

    /// <summary>
    /// Handles the joining process for the host player.
    /// </summary>
    public void HandleHostJoin(PlayerRef player)
    {
        isRemote = false;

        SetSessionMeta();
        ConfigureCamera();
        StartCoroutine(SpawnAvatarMarker(player));

        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
        MUES_RoomVisualizer.Instance.CaptureRoom();
    }

    /// <summary>
    /// Sets the session metadata with the provided anchor group UUID.
    /// </summary>
    public void SetSessionMeta()
    {
        ConsoleMessage.Send(debugMode, "Spawning Session Meta object...", Color.cyan);

        var spawnedMeta = Runner.Spawn(sessionMetaPrefab, Vector3.zero, Quaternion.identity, PlayerRef.None);

        if (spawnedMeta == null)
        {
            ConsoleMessage.Send(true, "[MUES_Networking] Failed to spawn Session Meta object!", Color.red);
            return;
        }

        var _sessionMeta = spawnedMeta.GetComponent<MUES_SessionMeta>();

        if (_sessionMeta == null || _sessionMeta.Object == null || !_sessionMeta.Object.HasStateAuthority)
        {
            ConsoleMessage.Send(debugMode, "Cannot set session meta - no state authority.", Color.red);
            return;
        }

        _sessionMeta.AnchorGroup = anchorGroupUuid.ToString();
        _sessionMeta.HostIP = LocalIPAddress();

        if (sceneParent != null)
        {
            _sessionMeta.UpdateHostSceneParentPose(sceneParent.position, sceneParent.rotation);
            ConsoleMessage.Send(debugMode, $"Session meta updated with sceneParent pose: pos={sceneParent.position}, rot={sceneParent.rotation.eulerAngles}", Color.cyan);
        }

        ConsoleMessage.Send(debugMode, $"Session meta set: AnchorGroup={_sessionMeta.AnchorGroup}, HostIP={_sessionMeta.HostIP}", Color.cyan);
    }

    #endregion

    #region Joining - Non-Host-Clients

    /// <summary>
    /// Gets executed when a trackable is added to the MRUK.
    /// </summary>
    private void OnTrackableAdded(MRUKTrackable trackable)
    {
        ConsoleMessage.Send(debugMode, "Trackable added.", Color.green);
        ScanQRCode(trackable);
    }

    /// <summary>
    /// When a trackable is added, check if it's a QR code and try to join the session.
    /// </summary>
    public void ScanQRCode(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode || trackable.MarkerPayloadString == null)
            return;

        string content = trackable.MarkerPayloadString;
        ConsoleMessage.Send(debugMode, $"Detected QR code: {content}", Color.green);

        var parts = content.Split('_');

        if (parts.Length < 2 || parts[0] != "MUESJoin")
        {
            ConsoleMessage.Send(debugMode, "Detected QR Code - invalid QR-format for session joining.", Color.red);
            return;
        }

        if (IsConnectedToRoom() || (Runner != null && Runner.IsSharedModeMasterClient) || isCreatingRoom)
        {
            ConsoleMessage.Send(debugMode, "Already connected to a session, not joining another.", Color.yellow);
            return;
        }

        JoinSessionFromCode(parts[1]);
    }

    /// <summary>
    /// Joins a session using the provided room token from the QR code.
    /// </summary>
    public async void JoinSessionFromCode(string roomToken)
    {
        isJoiningAsClient = true;
        var result = await JoinRoomByToken(roomToken);

        if (!result.IsSuccess)
            Debug.LogError($"Room join failed: {result.ErrorMessage}");
        else
            ConsoleMessage.Send(debugMode, "Joined room via QR code.", Color.green);
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

        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(true);

        var result = await runner.StartGame(startArgs);

        return new RoomOperationResult
        {
            ErrorMessage = result.Ok ? null : $"Failed to Start: {result.ShutdownReason}, Error Message: {result.ErrorMessage}",
            RoomToken = roomToken,
            RoomPassword = null
        };
    }

    /// <summary>
    /// Handles the joining process for non-host players.
    /// </summary>
    public IEnumerator HandleNonHostJoin(PlayerRef player)
    {
        yield return WaitForSessionMeta();

        if (Instance == null)
        {
            AbortJoin("Timeout waiting for Session Meta (10s). Cannot join session.");
            yield break;
        }

        var meta = MUES_SessionMeta.Instance;
        ConsoleMessage.Send(debugMode, $"Session Meta found. AnchorGroup: {meta.AnchorGroup}, HostIP: {meta.HostIP}, LocalIP: {LocalIPAddress()}", Color.cyan);

        yield return WaitForJoinEnabled(meta);

        if (!TryValidateAndParseSessionData(meta, out bool shouldAbort) && shouldAbort)
            yield break;

        ConfigureRemoteStatus(meta);

        if (!isRemote)
        {
            if (!TryLoadSharedAnchors())
                yield break;

            float anchorTimeout = 10f;
            float anchorElapsed = 0f;
            OVRSpatialAnchor loadedAnchor = null;

            while (anchorElapsed < anchorTimeout)
            {
                var anchorGO = GameObject.FindWithTag("RoomCenterAnchor");
                if (anchorGO != null)
                {
                    loadedAnchor = anchorGO.GetComponent<OVRSpatialAnchor>();
                    if (loadedAnchor != null && loadedAnchor.Localized)
                    {
                        anchorTransform = anchorGO.transform;
                        ConsoleMessage.Send(debugMode, $"Anchor localized at: {anchorTransform.position}", Color.green);
                        break;
                    }
                }

                ConsoleMessage.Send(debugMode, $"Waiting for anchor localization... (Localized={loadedAnchor?.Localized})", Color.yellow);
                anchorElapsed += Time.deltaTime;
                yield return null;
            }

            if (anchorTransform == null || (loadedAnchor != null && !loadedAnchor.Localized))
            {
                AbortJoin("Failed to localize shared spatial anchor within timeout.");
                yield break;
            }

            if (sceneParent == null) InitSceneParent();

            ConsoleMessage.Send(debugMode, $"Colocated client - anchor at {anchorTransform.position}, sceneParent at {sceneParent?.position}", Color.green);
        }

        ConfigureCamera();

        if (!TryLoadRoomData(meta, player))
        {
            AbortJoin("Failed to load room data.");
            yield break;
        }

        yield return SpawnAvatarMarker(player);
        yield return new WaitForSeconds(5f);

        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
        isConnected = true;

        // --- OBJECT INSTANTIATION TESTING - CLIENT ---
        var (spawnPos, spawnRot) = GetSpawnPoseInFrontOfCamera(0.75f);
        MUES_NetworkedObjectManager.Instance.Instantiate(MUES_NetworkedObjectManager.Instance.testModelPrefab, spawnPos, spawnRot, out _);
        // ---
    }

    /// <summary>
    /// Waits until the MUES_SessionMeta instance is available. Times out after 10 seconds.
    /// </summary>
    private IEnumerator WaitForSessionMeta()
    {
        float timeout = 10f;
        float elapsed = 0f;

        while (MUES_SessionMeta.Instance == null)
        {
            elapsed += Time.deltaTime;
            if (elapsed >= timeout)
            {
                AbortJoin("Timeout waiting for Session Meta (10s). Cannot join session.");
                yield break;
            }
            yield return null;
        }
    }

    /// <summary>
    /// Waits until joining is enabled in the session meta.
    /// </summary>
    private IEnumerator WaitForJoinEnabled(MUES_SessionMeta meta)
    {
        while (!meta.JoinEnabled)
        {
            ConsoleMessage.Send(debugMode, "Waiting for host to enable joining...", Color.yellow);
            yield return null;
        }
    }

    /// <summary>
    /// Attempts to validate and parse session metadata to determine if the session can proceed.
    /// </summary>
    private bool TryValidateAndParseSessionData(MUES_SessionMeta meta, out bool shouldAbort)
    {
        shouldAbort = false;
        string anchorGroupStr = meta.AnchorGroup.ToString();
        string hostIPStr = meta.HostIP.ToString();

        if (string.IsNullOrEmpty(anchorGroupStr) || string.IsNullOrEmpty(hostIPStr))
        {
            AbortJoin("Session Meta data is incomplete. Cannot load shared anchors.");
            shouldAbort = true;
            return false;
        }

        if (Guid.TryParse(anchorGroupStr, out Guid id) && id != Guid.Empty)
        {
            anchorGroupUuid = id;
            return true;
        }

        AbortJoin($"Invalid or empty AnchorGroup '{meta.AnchorGroup}' (len={meta.AnchorGroup.Length})");
        shouldAbort = true;
        return false;
    }

    /// <summary>
    /// Configures the remote status of the network based on the provided session metadata and network settings.
    /// </summary>
    private void ConfigureRemoteStatus(MUES_SessionMeta meta)
    {
        if (forceClientRemote)
        {
            isRemote = true;
            ConsoleMessage.Send(debugMode, "Forcing client to remote mode as per configuration.", Color.yellow);
        }
        else
            isRemote = !IsSameNetwork24(meta.HostIP.ToString(), LocalIPAddress());

        ConsoleMessage.Send(debugMode, $"Remote status determined: isRemote={isRemote}", Color.cyan);
    }

    /// <summary>
    /// Attempts to load and instantiate shared anchors for the specified anchor group.
    /// </summary>
    private bool TryLoadSharedAnchors()
    {
        if (anchorGroupUuid == Guid.Empty)
        {
            AbortJoin("No valid anchorGroupUuid set  cannot load shared anchors.");
            return false;
        }

        spatialAnchorCore.LoadAndInstantiateAnchorsFromGroup(roomMiddleAnchor, anchorGroupUuid);
        ConsoleMessage.Send(debugMode, $"Loading shared anchors for group: {anchorGroupUuid}", Color.cyan);
        return true;
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
            return;
        }

        ConsoleMessage.Send(debugMode, "Shared spatial anchors loaded after joining room.", Color.green);
    }

    /// <summary>
    /// Attempts to load room data for the specified session and player.
    /// </summary>
    private bool TryLoadRoomData(MUES_SessionMeta meta, PlayerRef player)
    {
        if (!isRemote)
        {
            ConsoleMessage.Send(debugMode, "Colocated client - skipping room data loading.", Color.cyan);
            return true;
        }

        var storedRoomData = meta.GetRoomData();

        if (storedRoomData != null)
        {
            ConsoleMessage.Send(debugMode, "Loading compressed room data from Session Meta...", Color.cyan);
            MUES_RoomVisualizer.Instance?.SetRoomData(storedRoomData);
            return true;
        }

        ConsoleMessage.Send(debugMode, "Loading cached room geometry for remote user (Legacy RPC fallback).", Color.cyan);
        var roomVis = MUES_RoomVisualizer.Instance;

        if (roomVis != null && roomVis.HasRoomData)
            roomVis.SendRoomDataTo(player);

        return true;
    }

    /// <summary>
    /// Aborts the join process with an error message and leaves the room.
    /// </summary>
    private void AbortJoin(string errorMessage)
    {
        ConsoleMessage.Send(debugMode, $"[MUES_NetworkingEvents] {errorMessage}", Color.red);
        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
        LeaveRoom();
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Gets a spawn position and rotation in front of the main camera.
    /// </summary>
    private (Vector3 position, Quaternion rotation) GetSpawnPoseInFrontOfCamera(float distance)
    {
        Vector3 camPos = mainCam.transform.position;
        Vector3 flatForward = Vector3.ProjectOnPlane(mainCam.transform.forward, Vector3.up).normalized;

        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;

        return (camPos + flatForward * distance, Quaternion.LookRotation(flatForward, Vector3.up));
    }

    /// <summary>
    /// Calculates a floor-aligned position from a transform, using the tracking space Y if available.
    /// </summary>
    public static Vector3 GetFloorAlignedPosition(Transform sourceTransform, float? overrideY = null)
    {
        float yPos = overrideY ?? 0f;

        if (!overrideY.HasValue)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            yPos = rig != null ? rig.trackingSpace.position.y : 0f;
        }

        return new Vector3(sourceTransform.position.x, yPos, sourceTransform.position.z);
    }

    /// <summary>
    /// Calculates a flat (Y-axis only) rotation from a transform's forward direction.
    /// </summary>
    public static Quaternion GetFlatRotation(Transform sourceTransform)
    {
        Vector3 flatForward = Vector3.ProjectOnPlane(sourceTransform.forward, Vector3.up).normalized;
        return flatForward.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(flatForward, Vector3.up)
            : Quaternion.identity;
    }

    /// <summary>
    /// Gets both floor-aligned position and flat rotation from a transform.
    /// </summary>
    public static (Vector3 position, Quaternion rotation) GetFloorAlignedPose(Transform sourceTransform, float? overrideY = null)
    {
        return (GetFloorAlignedPosition(sourceTransform, overrideY), GetFlatRotation(sourceTransform));
    }

    /// <summary>
    /// Finds or creates the SCENE_PARENT GameObject and returns its transform.
    /// </summary>
    public static Transform GetOrCreateSceneParent()
    {
        GameObject parent = GameObject.Find("SCENE_PARENT") ?? new GameObject("SCENE_PARENT");
        return parent.transform;
    }

    /// <summary>
    /// Initializes the NetworkRunner for the session.
    /// </summary>
    private NetworkRunner InitializeNetworkRunner()
    {
        if (_runnerPrefab == null)
            _runnerPrefab = FindFirstObjectByType<NetworkRunner>();

        if (_runnerPrefab != null && _runnerPrefab.gameObject != gameObject)
        {
            var runnerInstance = Instantiate(_runnerPrefab);
            _runnerPrefab.gameObject.SetActive(false);
            runnerInstance.name = "Session Runner";
            DontDestroyOnLoad(runnerInstance.gameObject);
            return runnerInstance;
        }

        GameObject go = new("Session Runner");
        var runner = go.AddComponent<NetworkRunner>();
        DontDestroyOnLoad(go);
        return runner;
    }

    /// <summary>
    /// Gets the current scene info for the NetworkRunner.
    /// </summary>
    private static NetworkSceneInfo GetSceneInfo()
    {
        var sceneInfo = new NetworkSceneInfo();
        if (TryGetActiveSceneRef(out var sceneRef) && sceneRef.IsValid)
            sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
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
    /// Returns the local IP address of the device.
    /// </summary>
    private string LocalIPAddress()
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
    private bool IsSameNetwork24(string ip1, string ip2)
    {
        if (string.IsNullOrWhiteSpace(ip1) || string.IsNullOrWhiteSpace(ip2))
            return false;

        var p1 = ip1.Split('.');
        var p2 = ip2.Split('.');

        return p1.Length == 4 && p2.Length == 4 && p1[0] == p2[0] && p1[1] == p2[1] && p1[2] == p2[2];
    }

    /// <summary>
    /// Returns whether the NetworkRunner is connected to a room.
    /// </summary>
    private bool IsConnectedToRoom()
    {
        foreach (var runner in NetworkRunner.Instances)
        {
            if (runner != null && runner.IsRunning)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Retrieves the transform of the room center (FLOOR object).
    /// </summary>
    public static Transform GetRoomCenter()
    {
        var go = GameObject.Find("FLOOR");
        return go != null ? go.transform : null;
    }

    /// <summary>
    /// Configures the main camera based on whether Insight Passthrough is enabled.
    /// </summary>
    public void ConfigureCamera()
    {
        OVRManager manager = OVRManager.instance;
        if (manager == null) return;

        manager.isInsightPassthroughEnabled = !isRemote;
        mainCam.allowHDR = isRemote;
        mainCam.clearFlags = isRemote ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;

        ConsoleMessage.Send(debugMode, $"Camera configured: isRemote={isRemote}, HDR={mainCam.allowHDR}, ClearFlags={mainCam.clearFlags}", Color.cyan);
    }

    /// <summary>
    /// Spawns the avatar marker for the given player.
    /// </summary>
    public IEnumerator SpawnAvatarMarker(PlayerRef player)
    {
        var marker = Runner.Spawn(playerMarkerPrefab, Vector3.zero, Quaternion.identity, Runner.LocalPlayer);

        marker.RequestStateAuthority();
        marker.AssignInputAuthority(player);
        Runner.SetPlayerObject(player, marker);

        yield return marker.GetComponent<MUES_AvatarMarker>().WaitForComponentInit();

        ConsoleMessage.Send(debugMode, $"Local player {player} spawned marker. StateAuthority={marker.StateAuthority}", Color.cyan);
    }

    /// <summary>
    /// Hindles the HMD mounted event to show loading text.
    /// </summary>
    private IEnumerator ShowLoadingOnHMDMounted()
    {
        if (!isConnected) yield break;

        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(true);
        yield return new WaitForSeconds(3f);
        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
    }

    /// <summary>
    /// Updates the scene parent transform based on the anchor transform, applying glitch filtering.
    /// </summary>
    private void UpdateSceneParent()
    {
        if (sceneParent == null || anchorTransform == null) return;

        Vector3 currentAnchorPos = GetFloorAlignedPosition(anchorTransform);
        Quaternion currentAnchorRot = GetFlatRotation(anchorTransform);

        if (_isFirstFrame)
        {
            _lastValidAnchorPos = currentAnchorPos;
            _lastValidAnchorRot = currentAnchorRot;
            _isFirstFrame = false;
        }

        float distance = Vector3.Distance(_lastValidAnchorPos, currentAnchorPos);
        Vector3 targetPos;
        Quaternion targetRot;

        if (distance > maxDistanceThreshold)
        {
            _glitchTimer += Time.deltaTime;

            if (_glitchTimer > glitchTimeThreshold)
            {
                targetPos = currentAnchorPos;
                targetRot = currentAnchorRot;
                _lastValidAnchorPos = currentAnchorPos;
                _lastValidAnchorRot = currentAnchorRot;
            }
            else
            {
                targetPos = _lastValidAnchorPos;
                targetRot = _lastValidAnchorRot;
            }
        }
        else
        {
            _glitchTimer = 0f;
            targetPos = currentAnchorPos;
            targetRot = currentAnchorRot;
            _lastValidAnchorPos = currentAnchorPos;
            _lastValidAnchorRot = currentAnchorRot;
        }

        sceneParent.position = Vector3.SmoothDamp(sceneParent.position, targetPos, ref _sceneVelocity, sceneParentPositionSmoothing);
        sceneParent.rotation = Quaternion.Slerp(sceneParent.rotation, targetRot, Time.deltaTime * sceneParentRotationSmoothing);
    }

    /// <summary>
    /// Checks if this client has become the new SharedModeMasterClient and handles migration.
    /// </summary>
    public void CheckIfNewMaster(PlayerRef player)
    {
        bool becameNewMaster = Runner.IsSharedModeMasterClient && _previousMasterClient != Runner.LocalPlayer;

        if (becameNewMaster)
        {
            ConsoleMessage.Send(debugMode, "Became new SharedModeMasterClient - taking over session objects...", Color.cyan);

            if (ShouldCloseRoom(Runner))
            {
                ConsoleMessage.Send(debugMode, "Only remote players left in the lobby. Shutting down.", Color.red);
                Runner.Shutdown();
                return;
            }

            ClaimOrphanedObjects(Runner);
            StartCoroutine(HandleMasterClientMigration(Runner, player));
        }

        if (!becameNewMaster || Runner.IsSharedModeMasterClient)
        {
            var playerObject = Runner.GetPlayerObject(player);
            if (playerObject != null && playerObject.TryGetComponent<MUES_AvatarMarker>(out _))
            {
                if (playerObject.HasStateAuthority || Runner.IsSharedModeMasterClient)
                {
                    Runner.Despawn(playerObject);
                    ConsoleMessage.Send(debugMode, $"Despawned avatar for leaving player {player}", Color.yellow);
                }
            }
        }

        if (Runner.IsSharedModeMasterClient)
            _previousMasterClient = Runner.LocalPlayer;
    }

    /// <summary>
    /// Handles the migration when this client becomes the new SharedModeMasterClient.
    /// </summary>
    public IEnumerator HandleMasterClientMigration(NetworkRunner runner, PlayerRef leftPlayer)
    {
        ConsoleMessage.Send(debugMode, "Master client migration started - taking over orphaned objects...", Color.cyan);
        yield return null;

        var objectsToProcess = new List<NetworkObject>();

        foreach (var obj in runner.GetAllNetworkObjects())
        {
            if (obj == null || !obj.IsValid) continue;

            if (obj.TryGetComponent<MUES_AvatarMarker>(out _) && (obj.InputAuthority == runner.LocalPlayer || obj.InputAuthority == leftPlayer))
                continue;

            objectsToProcess.Add(obj);
        }

        foreach (var obj in objectsToProcess)
        {
            if (obj != null && obj.IsValid && !obj.HasStateAuthority)
            {
                obj.RequestStateAuthority();
                ConsoleMessage.Send(debugMode, $"Migration: Requested StateAuthority for {obj.name}", Color.cyan);
            }
        }

        yield return new WaitForSeconds(0.5f);

        foreach (var obj in objectsToProcess)
        {
            if (obj == null || !obj.IsValid) continue;

            if (obj.HasStateAuthority && obj.InputAuthority == leftPlayer)
            {
                try
                {
                    obj.AssignInputAuthority(runner.LocalPlayer);
                    ConsoleMessage.Send(debugMode, $"Migration: Assigned InputAuthority for {obj.name}", Color.cyan);
                }
                catch (Exception ex)
                {
                    ConsoleMessage.Send(debugMode, $"Error assigning auth: {ex.Message}", Color.yellow);
                }
            }
        }

        yield return new WaitForSeconds(0.3f);

        foreach (var obj in objectsToProcess)
        {
            if (obj == null || !obj.IsValid) continue;

            if (obj.TryGetComponent<MUES_Chair>(out _) && obj.TryGetComponent<MUES_NetworkedTransform>(out var chairNetTransform))
            {
                chairNetTransform.RefreshGrabbableState();
                ConsoleMessage.Send(debugMode, $"Migration: Refreshed Chair grabbable via NetworkedTransform {obj.name}", Color.cyan);
            }

            if (obj.TryGetComponent<MUES_NetworkedTransform>(out var netTransform))
            {
                netTransform.TransferSpawnerOwnership();
                ConsoleMessage.Send(debugMode, $"Migration: Transfer SpawnerOwnership {obj.name}", Color.cyan);
            }
        }

        var meta = MUES_SessionMeta.Instance?.Object;
        if (meta != null && meta.IsValid && !meta.HasStateAuthority)
        {
            meta.RequestStateAuthority();
            ConsoleMessage.Send(debugMode, "Migration: Requested StateAuthority for SessionMeta", Color.cyan);
        }

        ConsoleMessage.Send(debugMode, "Master client migration completed", Color.green);
    }

    /// <summary>
    /// Immediately claims StateAuthority for all NetworkObjects that are now orphaned.
    /// </summary>
    private void ClaimOrphanedObjects(NetworkRunner runner)
    {
        foreach (var obj in runner.GetAllNetworkObjects())
        {
            if (obj == null || !obj.IsValid) continue;
            if (obj.TryGetComponent<MUES_AvatarMarker>(out _) && obj.InputAuthority == runner.LocalPlayer)
                continue;

            if (!obj.HasStateAuthority)
            {
                obj.RequestStateAuthority();
                ConsoleMessage.Send(debugMode, $"Claimed StateAuthority for orphaned object: {obj.name}", Color.cyan);
            }
        }

        if (MUES_SessionMeta.Instance != null && !MUES_SessionMeta.Instance.Object.HasStateAuthority)
        {
            MUES_SessionMeta.Instance.Object.RequestStateAuthority();
            ConsoleMessage.Send(debugMode, "Claimed StateAuthority for SessionMeta", Color.cyan);
        }
    }

    /// <summary>
    /// Checks if all remaining players in the session are remote (no colocated players left).
    /// </summary>
    public bool ShouldCloseRoom(NetworkRunner runner)
    {
        if (isRemote) return false;

        foreach (var p in runner.ActivePlayers)
        {
            var avatar = GetAvatarForPlayer(runner, p);
            if (avatar != null && !avatar.IsRemote)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Retrieves the avatar marker component for the specified player.
    /// </summary>
    public MUES_AvatarMarker GetAvatarForPlayer(NetworkRunner runner, PlayerRef player)
    {
        var playerObject = runner.GetPlayerObject(player);
        return playerObject != null ? playerObject.GetComponent<MUES_AvatarMarker>() : null;
    }

    /// <summary>
    /// Leaves the current room and shuts down all NetworkRunners.
    /// </summary>
    public void LeaveRoom()
    {
        for (int i = NetworkRunner.Instances.Count - 1; i >= 0; i--)
        {
            var runner = NetworkRunner.Instances[i];
            if (runner == null) continue;

            if (runner.IsRunning)
            {
                runner.Shutdown();
                Destroy(runner.gameObject);
            }
        }

        spatialAnchorCore.EraseAllAnchors();

        if (Runner != null && Runner.IsSharedModeMasterClient) _mruk.ClearScene();
        if (isRemote) Destroy(MUES_RoomVisualizer.Instance.virtualRoom);
        if (sceneParent != null) Destroy(sceneParent.gameObject);

        anchorGroupUuid = Guid.Empty;
        anchorTransform = sceneParent = null;
        heightCalibrationOffset = 0f;

        isConnected = isCreatingRoom = isJoiningAsClient = false;
        isRemote = false;

        ConfigureCamera();
        MUES_RoomVisualizer.Instance.HideSceneWhileLoading(false);
        MUES_RoomVisualizer.Instance.chairCount = 0;

        if (_runnerPrefab != null && !_runnerPrefab.gameObject.activeSelf)
        {
            _runnerPrefab.gameObject.SetActive(true);
            ConsoleMessage.Send(debugMode, "Re-enabled NetworkRunner prefab for future connections.", Color.cyan);
        }

        ConsoleMessage.Send(debugMode, "Left room.", Color.yellow);
    }

    #endregion

    #region Player Management

    /// <summary>
    /// Gets a list of all connected players in the session.
    /// </summary>
    public List<PlayerInfo> GetConnectedPlayers() => MUES_SessionMeta.Instance.GetAllPlayers();

    /// <summary>
    /// Gets a specific player by their PlayerRef.
    /// </summary>
    public PlayerInfo? GetPlayer(PlayerRef playerRef) => MUES_SessionMeta.Instance.GetPlayerByRef(playerRef);

    /// <summary>
    /// Kicks a player from the session by their PlayerRef.
    /// </summary>
    public void KickPlayer(PlayerRef playerRef)
    {
        if (Runner == null || !Runner.IsSharedModeMasterClient)
        {
            ConsoleMessage.Send(debugMode, "Only the host can kick players.", Color.red);
            return;
        }

        if (playerRef == Runner.LocalPlayer)
        {
            ConsoleMessage.Send(debugMode, "Cannot kick yourself.", Color.yellow);
            return;
        }

        var playerInfo = GetPlayer(playerRef);
        string playerName = playerInfo.HasValue ? playerInfo.Value.PlayerName.ToString() : "Unknown";

        Runner.Disconnect(playerRef);
        ConsoleMessage.Send(true, $"Player \"{playerName}\" (Ref: {playerRef}) has been kicked from the session.", Color.yellow);
    }

    /// <summary>
    /// Toggles the mute state for a player by their PlayerRef.
    /// </summary>
    public void ToggleMutePlayer(PlayerRef playerRef)
    {
        var playerObject = Runner?.GetPlayerObject(playerRef);
        var avatar = playerObject != null ? playerObject.GetComponent<MUES_AvatarMarker>() : null;

        if (avatar != null)
            SetMuteStatusForPlayer(playerRef, !avatar.IsMuted);
        else
            ConsoleMessage.Send(debugMode, $"Cannot toggle mute for player {playerRef} - avatar not found.", Color.red);
    }

    /// <summary>
    /// Mutes or unmutes a player by their PlayerRef. Only remote players can be muted.
    /// </summary>
    public void SetMuteStatusForPlayer(PlayerRef playerRef, bool mute)
    {
        if (playerRef == Runner?.LocalPlayer)
        {
            ConsoleMessage.Send(debugMode, "Use local mute controls to mute yourself.", Color.yellow);
            return;
        }

        var playerObject = Runner?.GetPlayerObject(playerRef);
        var avatar = playerObject != null ? playerObject.GetComponent<MUES_AvatarMarker>() : null;

        if (playerObject == null || avatar == null)
        {
            ConsoleMessage.Send(debugMode, $"Cannot mute player {playerRef} - avatar not found.", Color.red);
            return;
        }

        if (!avatar.IsRemote)
        {
            ConsoleMessage.Send(debugMode, $"Cannot mute player \"{avatar.PlayerName}\" - only remote players can be muted.", Color.yellow);
            return;
        }

        MUES_SessionMeta.Instance.SetPlayerMuted(playerRef, mute);
        avatar.voiceAudioSource.mute = mute;

        ConsoleMessage.Send(true, $"Player \"{avatar.PlayerName}\" has been {(mute ? "muted" : "unmuted")}.", Color.cyan);
    }

    #endregion
}

#if UNITY_EDITOR

[CustomEditor(typeof(MUES_Networking))]
public class MUES_NetworkingEditor : Editor
{
    private string joinToken = "";

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MUES_Networking networking = (MUES_Networking)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Networking Controls:", EditorStyles.boldLabel);

        joinToken = EditorGUILayout.TextField("Debug Room Token", joinToken);
        EditorGUILayout.Space();

        if (GUILayout.Button("Create room (Host)"))
        {
            if (Application.isPlaying) networking.StartLobbyCreation();
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

#endif
