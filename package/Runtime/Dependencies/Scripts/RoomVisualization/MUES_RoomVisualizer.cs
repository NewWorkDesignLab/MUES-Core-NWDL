using DG.Tweening;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using static OVRInput;
using static Oculus.Interaction.TransformerUtils;
using Fusion;

public class MUES_RoomVisualizer : MonoBehaviour
{
    [Header("General Settings:")]
    [Tooltip("Button to save the room after placement.")]
    public Button saveRoomButton = Button.Two;

    [Header("Object References:")]
    [Tooltip("Prefab for loading particles.")]
    public GameObject roomParticlesPrefab;
    [Tooltip("Prefab for the fallback teleport surface.")]
    public GameObject teleportSurface;
    [Tooltip("Material for floor and ceiling.")]
    public Material floorCeilingMat;
    [Tooltip("Prefab for chair placement visualization.")]
    public GameObject chairPrefab;
    [Tooltip("Networked prefab for chairs.")]
    public MUES_NetworkedTransform networkedChairPrefab;
    [Tooltip("Layer mask for floor raycasting during chair placement.")]
    public LayerMask floorLayer;

    [Header("Debug Settings:")]
    [Tooltip("Enables debug mode for displaying console messages.")]
    public bool debugMode = true;

    [HideInInspector] public bool HasRoomData => currentRoomData != null; // Indicates if room data is available.
    [HideInInspector] public bool chairPlacementActive => chairPlacement; // Indicates if chair placement mode is active.

    [HideInInspector] public List<MUES_Chair> chairsInScene = new(); // List to store chair transforms.
    [HideInInspector] public GameObject virtualRoom; // Root object for instantiated room geometry.
    [HideInInspector] public int chairCount = 0; // Count of chairs placed in the room.

    public RoomData GetCurrentRoomData() => currentRoomData; // Public getter for networking

    private ParticleSystem _particleSystem; // Reference to the ParticleSystem component.
    private ParticleSystemRenderer _particleSystemRenderer; // Reference to the ParticleSystemRenderer component.
    private AnchorPrefabSpawner prefabSpawner;  // Reference to the AnchorPrefabSpawner component.
    private int originalCullingMask;    // Original culling mask for the main camera.

    private RoomData currentRoomData;  // Data structure to hold captured room data.
    private List<Transform> instantiatedRoomPrefabs = new();    // List to store instantiated room prefabs.

    private List<Transform> currentTableTransforms = new(); // List to store table transforms.
    private GameObject floor; // Reference to the floor object.

    private GameObject previewChair; // Reference to the preview chair GameObject.
    private Transform rightController;  // Reference to the right controller transform.

    private bool chairPlacement, sceneShown, chairAnimInProgress = false;  // State variables for scene visualization.
    private readonly List<GameObject> roomPrefabs = new();   // List to store room prefabs. (Same order as in AnchorPrefabSpawner)

    public static float floorHeight = 0f; // Static variable to hold the floor height.
    public static MUES_RoomVisualizer Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;

        var debugger = FindFirstObjectByType<ImmersiveSceneDebugger>();

        if (debugger && isActiveAndEnabled)
        {
            debugger.gameObject.SetActive(false);
            Debug.Log("[MUES_RoomVisualizer] Disabled ImmersiveSceneDebugger to prevent conflicts.");
        }

        rightController = GameObject.Find("RightHandAnchor").transform;
    }

    void Start()
    {
        prefabSpawner = GetComponent<AnchorPrefabSpawner>();
        originalCullingMask = Camera.main.cullingMask;

        foreach (var prefab in prefabSpawner.PrefabsToSpawn)
            roomPrefabs.Add(prefab.Prefabs[0]);
    }

    private void Update()
    {
        var net = MUES_Networking.Instance;
        if (!net.isConnected && net.Runner != null && !net.Runner.IsSharedModeMasterClient) return;

        if (!chairPlacement || previewChair == null) return;

        Ray ray = new(rightController.transform.position, rightController.transform.forward);
        bool rayHit = Physics.Raycast(ray, out RaycastHit hitInfo, 10, floorLayer);

        previewChair.SetActive(rayHit);

        if (GetDown(saveRoomButton)) FinalizeRoomData();
        if (GetDown(RawButton.RIndexTrigger, Controller.RTouch) && chairCount < net.maxPlayers && rayHit && !chairAnimInProgress)
            StartCoroutine(PlaceChair(hitInfo.point, previewChair.transform.localScale));

        if (previewChair.activeSelf)
        {
            Vector3 smoothedTargetPosition = Vector3.Lerp(previewChair.transform.position, hitInfo.point, Time.deltaTime * 15);
            previewChair.transform.SetPositionAndRotation(smoothedTargetPosition, GetRotationTowardsNearestTable(smoothedTargetPosition));
        }
    }

    #region Scene Mesh Data Serialization - Capture

    /// <summary>
    /// Captures the room by loading the scene from the device. (HOST ONLY)
    /// </summary>
    public void CaptureRoom() => StartCoroutine(CaptureRoomRoutine());

    /// <summary>
    /// Coroutine to capture the scene. (HOST ONLY)
    /// </summary>  
    private IEnumerator CaptureRoomRoutine()
    {
        yield return new WaitForEndOfFrame();

        var room = FindFirstObjectByType<MRUKRoom>();
        if (room == null)
        {
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] No MRUKRoom found in scene! Can't capture room!", Color.red);
            MUES_Networking.Instance.LeaveRoom();
            yield break;
        }

        room.transform.localScale = Vector3.one;

        var net = MUES_Networking.Instance;
        Transform referenceTransform = net?.sceneParent ?? room.transform;
        
        if (net?.sceneParent != null)
            Debug.Log($"[MUES_RoomVisualizer] Capturing room relative to SceneParent: {referenceTransform.name}");
        else
            Debug.LogWarning("[MUES_RoomVisualizer] Capturing room relative to Room transform (SceneParent not found), this may cause misalignment.");

        var anchors = room.Anchors;
        var anchorTransformDataList = new List<AnchorTransformData>(anchors.Count);
        var _floorCeilingData = new FloorCeilingData();

        foreach (var anchor in anchors)
        {
            if (anchor == null || anchor.transform == null || anchor.transform.childCount == 0) continue;

            var anchorData = new TransformationData(
                referenceTransform.InverseTransformPoint(anchor.transform.position),
                Quaternion.Inverse(referenceTransform.rotation) * anchor.transform.rotation,
                anchor.transform.localScale);

            var prefab = anchor.transform.GetChild(0);

            var prefabData = new TransformationData(
                prefab.transform.localPosition,
                prefab.transform.localRotation,
                prefab.transform.localScale);

            var entry = new AnchorTransformData
            {
                name = anchor.name,
                type = GetTypeFromLabel(anchor),
                anchorTransform = anchorData,
                prefabTransform = prefabData
            };

            if (anchor.name == "FLOOR" || anchor.name == "CEILING")
            {
                var mf = prefab.GetComponent<MeshFilter>();
                if (mf?.sharedMesh != null)
                {
                    var mesh = mf.sharedMesh;
                    var verts = mesh.vertices;
                    var norms = mesh.normals;
                    var uvs = mesh.uv;
                    int vCount = verts.Length;
                    var vertexArray = new VertexData[vCount];

                    bool hasNorms = norms != null && norms.Length == vCount;
                    bool hasUvs = uvs != null && uvs.Length == vCount;

                    for (int i = 0; i < vCount; i++)
                        vertexArray[i] = new VertexData(verts[i], hasNorms ? norms[i] : Vector3.up, hasUvs ? uvs[i] : Vector2.zero);

                    if (anchor.name == "FLOOR")
                    {
                        _floorCeilingData.floorVertices = vertexArray;
                        _floorCeilingData.floorTriangles = mesh.triangles;
                        floorHeight = anchor.transform.position.y;
                    }
                    else
                    {
                        _floorCeilingData.ceilingVertices = vertexArray;
                        _floorCeilingData.ceilingTriangles = mesh.triangles;
                    }
                }
            }

            anchorTransformDataList.Add(entry);
        }

        currentRoomData = new RoomData
        {
            anchorTransformData = anchorTransformDataList.ToArray(),
            floorCeilingData = _floorCeilingData
        };

        currentTableTransforms = FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.name == "TABLE")
            .ToList();

        foreach (var table in currentTableTransforms)
        {
            table.SetParent(net.sceneParent, true);
            table.GetComponent<MRUKAnchor>().enabled = false;
            table.localScale = Vector3.zero;
        }

        Transform floorTransform = MUES_Networking.GetRoomCenter();

        if (floorTransform == null)
        {
            Debug.LogError("[MUES_RoomVisualizer] CaptureRoomRoutine: Room center transform is null!");
            net.LeaveRoom();
            yield break;
        }

        floor = floorTransform.GetChild(0).gameObject;
        floor.transform.GetComponent<Renderer>().enabled = false;
        floor.transform.parent.SetParent(net.sceneParent, true);
        floor.transform.parent.GetComponent<MRUKAnchor>().enabled = false;

        var rb = floor.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        floor.layer = floorLayer;

        Destroy(room.gameObject);
        yield return SwitchToChairPlacement(true);
    }

    /// <summary>
    /// Finalizes the room data by capturing chair transformations. (HOST ONLY)
    /// </summary>
    private void FinalizeRoomData()
    {
        foreach (var item in chairsInScene)
            item.GetComponent<MUES_AnchoredNetworkBehaviour>().ForceUpdateAnchorOffset();

        StartCoroutine(SwitchToChairPlacement(false));
        MUES_Networking.Instance.EnableJoining();
    }

    /// <summary>
    /// Returns an integer type based on the anchor's label. (HOST ONLY)
    /// </summary>
    int GetTypeFromLabel(MRUKAnchor anchor)
    {
        if (anchor.Label == MRUKAnchor.SceneLabels.FLOOR || anchor.Label == MRUKAnchor.SceneLabels.CEILING)
            return -1;

        return prefabSpawner.PrefabsToSpawn.FindIndex(prefabEntry => prefabEntry.Labels == anchor.Label);
    }

    #endregion

    #region Chair Placement Methods

    /// <summary>
    /// Switches to chair placement mode with animation. (HOST ONLY)
    /// </summary>
    private IEnumerator SwitchToChairPlacement(bool enabled)
    {
        if (!enabled) chairPlacement = false;
        else
        {
            yield return null;
            previewChair = Instantiate(chairPrefab);
            RenderRoomGeometry(true);
        }

        Sequence seq = DOTween.Sequence();
        foreach (var table in currentTableTransforms)
        {
            if (table == null) continue;

            if (table.TryGetComponent<ScalableTableComponent>(out var scalableTable))
                Destroy(scalableTable);

            seq.Join(table.DOScale(enabled ? Vector3.one : Vector3.zero, .35f).SetEase(Ease.OutExpo));
        }

        yield return seq.WaitForCompletion();

        if (enabled) chairPlacement = true;
        else
        {
            Destroy(previewChair);
            Destroy(floor.transform.parent.gameObject);

            foreach (var table in currentTableTransforms)
                Destroy(table.gameObject);

            currentTableTransforms.Clear();
        }
    }

    /// <summary>
    /// Places a chair at the specified position with animation. (HOST ONLY)
    /// </summary>
    private IEnumerator PlaceChair(Vector3 position, Vector3 targetScale, Quaternion? rotation = null)
    {
        chairAnimInProgress = true;

        try
        {
            Quaternion finalRot = rotation ?? previewChair.transform.rotation;
            MUES_NetworkedObjectManager.Instance.Instantiate(networkedChairPrefab, position, finalRot, out MUES_NetworkedTransform spawnedObj);

            float timeout = 1f;
            float elapsed = 0f;

            while (spawnedObj == null && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (spawnedObj == null)
            {
                Debug.LogWarning("[MUES_RoomVisualizer] Chair spawn timed out or failed.");
                yield break;
            }

            if (spawnedObj.TryGetComponent<MUES_Chair>(out var existingChair))
                chairsInScene.Add(existingChair);

            spawnedObj.transform.localScale = Vector3.zero;
            var gft = spawnedObj.transform.GetComponent<GrabFreeTransformer>();

            gft.InjectOptionalPositionConstraints(new PositionConstraints
            {
                ConstraintsAreRelative = false,
                XAxis = ConstrainedAxis.Unconstrained,
                ZAxis = ConstrainedAxis.Unconstrained,
                YAxis = new ConstrainedAxis
                {
                    ConstrainAxis = true,
                    AxisRange = new FloatRange
                    {
                        Min = spawnedObj.transform.position.y,
                        Max = spawnedObj.transform.position.y
                    }
                }
            });

            yield return spawnedObj.transform.DOScale(targetScale, 0.3f).SetEase(Ease.OutExpo).WaitForCompletion();
        }
        finally
        {
            chairCount++;
            chairAnimInProgress = false;
        }
    }

    /// <summary>
    /// Gets the rotation towards the nearest table for chair placement. (HOST ONLY)
    /// </summary>
    private Quaternion GetRotationTowardsNearestTable(Vector3 chairPosition)
    {
        Transform nearestTable = null;
        float nearestDistSq = float.MaxValue;

        foreach (var table in currentTableTransforms)
        {
            if (table == null) continue;

            float distSq = (table.position - chairPosition).sqrMagnitude;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestTable = table;
            }
        }

        if (nearestTable == null)
        {
            Debug.LogWarning("[MUES_RoomVisualizer] GetRotationTowardsNearestTable: no table found, using identity.");
            return Quaternion.identity;
        }

        Vector3 dir = nearestTable.position - chairPosition;
        dir.y = 0f;

        if (dir.sqrMagnitude < 0.0001f)
            return Quaternion.identity;

        return Quaternion.LookRotation(dir.normalized, Vector3.up);
    }

    #endregion

    #region Scene Mesh Data Serialization - Place

    /// <summary>
    /// Loads a room from room data.
    /// </summary>
    public void InstantiateRoomGeometry()
    {
        if (currentRoomData == null)
        {
            Debug.LogError($"[MUES_CubicRoomVisualizer] No data provided! Can't load room!");
            return;
        }

        ClearRoomVisualization();

        virtualRoom = new("InstantiatedRoom");

        foreach (var data in currentRoomData.anchorTransformData)
        {
            if (data.name == "FLOOR")
            {
                floorHeight = virtualRoom.transform.TransformPoint(data.anchorTransform.ToPosition()).y;
                break;
            }
        }

        var net = MUES_Networking.Instance;
        if (net != null)
        {
            if (net.sceneParent == null) net.InitSceneParent();

            if (net.sceneParent != null)
            {
                virtualRoom.transform.SetParent(net.sceneParent, false);
                virtualRoom.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                virtualRoom.transform.localScale = Vector3.one;

                if (net.isRemote)
                    ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] InstantiateRoomGeometry: Remote client - Parented virtualRoom to SceneParent.", Color.green);
            }
            else
            {
                virtualRoom.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] InstantiateRoomGeometry: SceneParent null even after Init attempt. Placing at origin.", Color.yellow);
            }
        }
        else
        {
            virtualRoom.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] InstantiateRoomGeometry: Networking null. Placing at origin.", Color.red);
        }

        foreach (var data in currentRoomData.anchorTransformData)
        {
            GameObject anchorInstance = new(data.name);
            anchorInstance.transform.SetParent(virtualRoom.transform);
            anchorInstance.transform.SetLocalPositionAndRotation(data.anchorTransform.ToPosition(), data.anchorTransform.ToRotation());
            anchorInstance.transform.localScale = data.anchorTransform.ToScale();

            GameObject prefabInstance;

            if (data.type >= 0)
                prefabInstance = Instantiate(roomPrefabs[data.type], anchorInstance.transform);
            else
            {
                prefabInstance = new GameObject();
                prefabInstance.transform.SetParent(anchorInstance.transform);
            }

            prefabInstance.transform.SetLocalPositionAndRotation(data.prefabTransform.ToPosition(), data.prefabTransform.ToRotation());
            prefabInstance.transform.localScale = data.prefabTransform.ToScale();

            if (data.type == -1)
            {
                bool isFloor = data.name == "FLOOR";
                prefabInstance.name = isFloor ? "Floor" : "Ceiling";

                VertexData[] vertexDataArray = isFloor ? currentRoomData.floorCeilingData.floorVertices : currentRoomData.floorCeilingData.ceilingVertices;
                int[] tris = isFloor ? currentRoomData.floorCeilingData.floorTriangles : currentRoomData.floorCeilingData.ceilingTriangles;

                MeshFilter mf = prefabInstance.AddComponent<MeshFilter>();
                mf.sharedMesh = VertexData.CreateMeshFromVertexData(vertexDataArray, tris);
                mf.sharedMesh.name = isFloor ? "FloorMesh" : "CeilingMesh";

                prefabInstance.AddComponent<MeshRenderer>().material = floorCeilingMat;
                prefabInstance.AddComponent<MeshCollider>();

                if (isFloor)
                    prefabInstance.layer = LayerMask.NameToLayer("Floor");
            }

            instantiatedRoomPrefabs.Add(anchorInstance.transform);
        }

        Debug.Log($"<color=lime>[MUES_CubicRoomVisualizer] Instantiated {instantiatedRoomPrefabs.Count} anchors from room data.</color>");

        InitializeVisuals();
    }

    /// <summary>
    /// Teleports the local OVRCameraRig to the first available chair in the scene. (REMOTE CLIENT ONLY)    
    /// </summary>
    public IEnumerator TeleportToFirstFreeChair()
    {
        var net = MUES_Networking.Instance;
        if (net == null || !net.isRemote)
        {
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] TeleportToFirstFreeChair skipped - not a remote client.", Color.yellow);
            yield break;
        }

        yield return new WaitUntil(() => net.isConnected);

        float timeout = 5f;
        float elapsed = 0f;

        while (chairsInScene.Count == 0 && elapsed < timeout)
        {
            var foundChairs = FindObjectsByType<MUES_Chair>(FindObjectsSortMode.None);
            if (foundChairs != null && foundChairs.Length > 0)
            {
                chairsInScene.AddRange(foundChairs);
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (chairsInScene.Count == 0)
        {
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] No chairs in scene to teleport to.", Color.yellow);
            yield break;
        }

        ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Looking for free chair among {chairsInScene.Count} chairs...", Color.cyan);

        var targetChair = chairsInScene.FirstOrDefault(c => c != null && !c.IsOccupied)?.transform;
        Vector3 targetPosition;

        if (targetChair != null)
        {
            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Teleporting to chair at position {targetChair.position}.", Color.green);
            targetPosition = targetChair.position;
        }
        else
        {
            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] No free chair found, teleporting to room center and activating teleport surface.", Color.yellow);
            Transform roomCenter = MUES_Networking.GetRoomCenter();
            targetPosition = roomCenter.position;
            Instantiate(teleportSurface, targetPosition, Quaternion.identity, roomCenter);
        }

        var ovrManager = OVRManager.instance;
        var rig = ovrManager.GetComponent<OVRCameraRig>();

        if (rig != null && Camera.main != null)
        {
            Vector3 headPos = Camera.main.transform.position;
            Vector3 rigPos = ovrManager.transform.position;
            Vector3 horizontalOffset = new(headPos.x - rigPos.x, 0f, headPos.z - rigPos.z);

            ovrManager.transform.position = new Vector3(
                targetPosition.x - horizontalOffset.x,
                targetPosition.y,
                targetPosition.z - horizontalOffset.z
            );

            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Teleported with offset compensation. Head offset: {horizontalOffset}", Color.green);
        }
        else
            ovrManager.transform.SetPositionAndRotation(targetPosition, ovrManager.transform.rotation);
    }

    #endregion

    #region Networking Methods

    /// <summary>
    /// Sends the captured room data to other clients.
    /// </summary>
    public void SendRoomDataTo(PlayerRef player)
    {
        if (!MUES_Networking.Instance.Runner.IsSharedModeMasterClient)
        {
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] SendRoomDataTo: no StateAuthority.", Color.red);
            return;
        }

        string json = JsonUtility.ToJson(currentRoomData);
        ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Sending room data to player {player}...", Color.cyan);
        currentRoomData = null;

        RPC_ReceiveRoomDataForPlayer(player, json);
    }

    /// <summary>
    /// Receives room data for a specific player and instantiates the geometry.
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_ReceiveRoomDataForPlayer([RpcTarget] PlayerRef targetPlayer, string json, RpcInfo info = default) => SetRoomDataFromJson(json);

    /// <summary>
    /// Sets the current room data from a JSON string and instantiates geometry.
    /// </summary>
    public void SetRoomDataFromJson(string json)
    {
        currentRoomData = JsonUtility.FromJson<RoomData>(json);
        ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Room data received via JSON, instantiating geometry...", Color.green);
        InstantiateRoomGeometry();
    }

    /// <summary>
    /// Sets the current room data directly.
    /// </summary>
    public void SetRoomData(RoomData data)
    {
        currentRoomData = data;
        ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Room data object set directly, instantiating geometry...", Color.green);
        InstantiateRoomGeometry();
    }

    #endregion

    #region Scene Visualizer Methods

    /// <summary>
    /// Instantiates loading visuals.
    /// </summary>
    private void InitializeVisuals()
    {
        Transform floorTransform = virtualRoom?.transform.Find("FLOOR") ?? GameObject.Find("FLOOR")?.transform;

        if (floorTransform != null)
        {
            var psGO = Instantiate(roomParticlesPrefab, floorTransform);
            _particleSystem = psGO.GetComponent<ParticleSystem>();
            _particleSystemRenderer = _particleSystem.GetComponent<ParticleSystemRenderer>();

            var shape = _particleSystem.shape;

            _particleSystem.transform.SetPositionAndRotation(transform.InverseTransformPoint(floorTransform.position - new Vector3(0, 1, 0)), floorTransform.rotation);
            _particleSystem.transform.SetParent(floorTransform);

            Transform firstChild = floorTransform.GetChild(0);
            shape.radius = firstChild.GetComponent<MeshRenderer>().bounds.size.magnitude * 5;

            var emission = _particleSystem.emission;
            emission.rateOverTime = firstChild.transform.localScale.magnitude;

            _particleSystem.Play();
        }
        else
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] InitializeVisuals: Could not find FLOOR anchor! Particle effects skipped.", Color.yellow);

        foreach (var anchor in instantiatedRoomPrefabs)
            if (anchor != null) anchor.localScale = Vector3.zero;

        sceneShown = false;
        ToggleVisualization();

        StartCoroutine(TeleportToFirstFreeChair());
    }

    /// <summary>
    /// Toggles the visualization of the scene.
    /// </summary>
    public void ToggleVisualization()
    {
        sceneShown = !sceneShown;
        StartCoroutine(ToggleVisualizationRoutine(sceneShown));
    }

    /// <summary>
    /// Coroutine to toggle the visualization of the scene.
    /// </summary>
    private IEnumerator ToggleVisualizationRoutine(bool isActive)
    {
        if (isActive && _particleSystemRenderer != null) _particleSystemRenderer.enabled = true;

        Sequence seq = DOTween.Sequence();
        foreach (var anchor in instantiatedRoomPrefabs)
        {
            if (anchor == null || anchor.transform == null) continue;

            seq.Join(anchor.transform.DOScale(isActive ? Vector3.one : Vector3.zero, 0.5f)
                .SetEase(isActive ? Ease.OutExpo : Ease.InExpo));
        }

        yield return seq.WaitForCompletion();

        if (!isActive && _particleSystemRenderer != null) _particleSystemRenderer.enabled = false;
    }

    /// <summary>
    /// Clears the current room visualization.
    /// </summary>
    public void ClearRoomVisualization()
    {
        foreach (var old in instantiatedRoomPrefabs)
            if (old != null && old.transform != null)
                Destroy(old.transform.gameObject);

        instantiatedRoomPrefabs.Clear();
        chairsInScene.Clear();

        if (virtualRoom != null)
        {
            Destroy(virtualRoom);
            virtualRoom = null;
        }
    }

    /// <summary>
    /// Renders or hides the room geometry by adjusting the camera's culling mask.
    /// </summary>
    public void RenderRoomGeometry(bool render)
    {
        int combinedMask = LayerMask.GetMask("RoomGeometry", "Wall");
        Camera cam = Camera.main;

        if (render) cam.cullingMask |= combinedMask;
        else cam.cullingMask &= ~combinedMask;
    }

    /// <summary>
    /// Toggles the visibility of the scene while a loading process is in progress.
    /// </summary>
    public void HideSceneWhileLoading(bool hide)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        cam.cullingMask = hide ? LayerMask.GetMask("RenderWhileLoading") : originalCullingMask | LayerMask.GetMask("Floor");
    }

    #endregion
}

#region Data Classes

[Serializable]
public class RoomData
{
    public AnchorTransformData[] anchorTransformData;
    public FloorCeilingData floorCeilingData;
}

[Serializable]
public class AnchorTransformData
{
    public string name;
    public int type;
    public TransformationData anchorTransform;
    public TransformationData prefabTransform;
}

[Serializable]
public class TransformationData
{
    public float[] localPosition = new float[3];
    public float[] localRotation = new float[4];
    public float[] localScale = new float[3];

    public TransformationData(Vector3 givenLocalPosition, Quaternion givenLocalRotation, Vector3 givenLocalScale)
    {
        localPosition[0] = givenLocalPosition.x;
        localPosition[1] = givenLocalPosition.y;
        localPosition[2] = givenLocalPosition.z;

        localRotation[0] = givenLocalRotation.x;
        localRotation[1] = givenLocalRotation.y;
        localRotation[2] = givenLocalRotation.z;
        localRotation[3] = givenLocalRotation.w;

        localScale[0] = givenLocalScale.x;
        localScale[1] = givenLocalScale.y;
        localScale[2] = givenLocalScale.z;
    }

    public Vector3 ToPosition() => new(localPosition[0], localPosition[1], localPosition[2]);
    public Quaternion ToRotation() => new(localRotation[0], localRotation[1], localRotation[2], localRotation[3]);
    public Vector3 ToScale() => new(localScale[0], localScale[1], localScale[2]);
}

[Serializable]
public class FloorCeilingData
{
    public VertexData[] floorVertices;
    public int[] floorTriangles;

    public VertexData[] ceilingVertices;
    public int[] ceilingTriangles;
}

[Serializable]
public class VertexData
{
    public float[] position = new float[3];
    public float[] normal = new float[3];
    public float[] uv = new float[2];

    public VertexData(Vector3 givenPosition, Vector3 givenNormal, Vector2 givenUV)
    {
        position[0] = givenPosition.x;
        position[1] = givenPosition.y;
        position[2] = givenPosition.z;

        normal[0] = givenNormal.x;
        normal[1] = givenNormal.y;
        normal[2] = givenNormal.z;

        uv[0] = givenUV.x;
        uv[1] = givenUV.y;
    }

    public static Mesh CreateMeshFromVertexData(VertexData[] vertexData, int[] triangles)
    {
        if (vertexData == null || vertexData.Length == 0)
            return null;

        int vCount = vertexData.Length;
        Vector3[] verts = new Vector3[vCount];
        Vector3[] norms = new Vector3[vCount];
        Vector2[] uvs = new Vector2[vCount];

        for (int i = 0; i < vCount; i++)
        {
            var v = vertexData[i];
            verts[i] = new Vector3(v.position[0], v.position[1], v.position[2]);
            norms[i] = new Vector3(v.normal[0], v.normal[1], v.normal[2]);
            uvs[i] = new Vector2(v.uv[0], v.uv[1]);
        }

        Mesh mesh = new()
        {
            vertices = verts,
            normals = norms,
            uv = uvs
        };

        if (triangles != null && triangles.Length > 0)
            mesh.triangles = triangles;

        mesh.RecalculateBounds();
        mesh.RecalculateNormals();

        return mesh;
    }
}

#endregion