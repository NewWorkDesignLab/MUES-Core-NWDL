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

namespace MUES.Core
{
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
        [Tooltip("Size of the invisible floor plane for custom room models.")]
        public Vector2 customRoomFloorSize = new Vector2(20f, 20f);

        [Header("Debug Settings:")]
        [Tooltip("Enables debug mode for displaying console messages.")]
        public bool debugMode = true;

        [HideInInspector] public bool HasRoomData => currentRoomData != null; // Indicates if room data is available.
        [HideInInspector] public bool chairPlacementActive => chairPlacement; // Indicates if chair placement mode is active.

        [HideInInspector] public List<MUES_Chair> chairsInScene = new(); // List to store chair transforms.
        [HideInInspector] public GameObject virtualRoom; // Root object for instantiated room geometry.
        [HideInInspector] public int chairCount = 0; // Count of chairs placed in the room.

        private const float DefaultTimeout = 7f; // Default timeout for waiting operations.

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

        private OVRSpatialAnchor _remoteLocalAnchor; // Local spatial anchor for remote clients (analogous to shared spatial anchor for colocated)
        private Transform remoteSceneParent; // Scene parent for remote clients - mirrors MUES_Networking.sceneParent behavior
        private MUES_SceneParentStabilizer _remoteStabilizer; // Shared stabilizer utility for remote clients

        private MUES_Networking Net => MUES_Networking.Instance; // Single source of truth for networking instance.

        public static float floorHeight = 0f; // Static variable to hold the floor height.
        public static MUES_RoomVisualizer Instance { get; private set; }

        #region Events

        /// <summary>
        /// Fired when the loading screen is shown (culling mask updated to hide scene).
        /// </summary>
        public static event Action OnLoadingStarted;

        /// <summary>
        /// Fired when the loading screen is hidden (culling mask restored).
        /// </summary>
        public static event Action OnLoadingEnded;

        /// <summary>
        /// Fired when chair placement mode is enabled.
        /// </summary>
        public static event Action OnChairPlacementStarted;

        /// <summary>
        /// Fired when chair placement mode is disabled (room finalized).
        /// </summary>
        public static event Action OnChairPlacementEnded;

        /// <summary>
        /// Fired when a chair is placed. Provides the chair transform.
        /// </summary>
        public static event Action<Transform> OnChairPlaced;

        /// <summary>
        /// Fired when room geometry rendering is toggled. Provides the render state.
        /// </summary>
        public static event Action<bool> OnRoomGeometryRenderChanged;

        /// <summary>
        /// Fired when the remote client has completed teleporting to a chair (or room center fallback).
        /// </summary>
        public static event Action OnTeleportCompleted;

        #endregion

        private void Awake()
        {
            if (Instance == null) Instance = this;

            _remoteStabilizer = new MUES_SceneParentStabilizer();

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
            if (!Net.isConnected && Net.Runner != null && !Net.Runner.IsSharedModeMasterClient) return;
            if (!chairPlacement || previewChair == null) return;

            Ray ray = new(rightController.transform.position, rightController.transform.forward);
            bool rayHit = Physics.Raycast(ray, out RaycastHit hitInfo, 10, floorLayer);

            previewChair.SetActive(rayHit);

            if (GetDown(saveRoomButton)) FinalizeRoomData();
            if (GetDown(RawButton.RIndexTrigger, Controller.RTouch) && chairCount < Net.maxPlayers && rayHit && !chairAnimInProgress)
                StartCoroutine(PlaceChair(hitInfo.point, previewChair.transform.localScale));

            if (previewChair.activeSelf)
            {
                Vector3 smoothedTargetPosition = Vector3.Lerp(previewChair.transform.position, hitInfo.point, Time.deltaTime * 15);
                previewChair.transform.SetPositionAndRotation(smoothedTargetPosition, GetRotationTowardsNearestTable(smoothedTargetPosition));
            }
        }

        private void LateUpdate() => UpdateRemoteSceneParent();

        #region Public API

        /// <summary>
        /// Returns the current room data.
        /// </summary>
        public RoomData GetCurrentRoomData() => currentRoomData;

        /// <summary>
        /// Gets the remote scene parent transform. Returns null if not a remote client.
        /// </summary>
        public Transform GetRemoteSceneParent() => remoteSceneParent;

        /// <summary>
        /// Captures the room by loading the scene from the device. (HOST ONLY)
        /// </summary>
        public void CaptureRoom() => StartCoroutine(CaptureRoomRoutine());

        /// <summary>
        /// Loads a room from room data and instantiates geometry.
        /// </summary>
        public void InstantiateRoomGeometry()
        {
            if (currentRoomData == null)
            {
                Debug.LogError("[MUES_RoomVisualizer] No data provided! Can't load room!");
                return;
            }

            ClearRoomVisualization();
            virtualRoom = new GameObject("InstantiatedRoom");

            SetFloorHeight();
            ParentVirtualRoom();
            InstantiateAnchors();

            Debug.Log($"<color=lime>[MUES_RoomVisualizer] Instantiated {instantiatedRoomPrefabs.Count} anchors from room data.</color>");
            InitializeVisuals();
        }

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

        /// <summary>
        /// Toggles the visualization of the scene.
        /// </summary>
        public void ToggleVisualization()
        {
            sceneShown = !sceneShown;
            StartCoroutine(ToggleVisualizationRoutine(sceneShown));
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

            if (_remoteLocalAnchor != null)
            {
                try
                {
                    var anchorGO = _remoteLocalAnchor.gameObject;
                    Destroy(_remoteLocalAnchor);
                    _remoteLocalAnchor = null;

                    if (anchorGO != null)
                        Destroy(anchorGO);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MUES_RoomVisualizer] Error during anchor cleanup: {ex.Message}");
                    _remoteLocalAnchor = null;
                }
            }

            if (remoteSceneParent != null)
            {
                Destroy(remoteSceneParent.gameObject);
                remoteSceneParent = null;
            }

            _remoteStabilizer.Reset();
        }

        /// <summary>
        /// Renders or hides the room geometry by adjusting the camera's culling mask.
        /// </summary>
        public void RenderRoomGeometry(bool render)
        {
            int combinedMask = LayerMask.GetMask("MUES_RoomGeometry", "MUES_Wall");
            Camera cam = Camera.main;

            if (render) cam.cullingMask |= combinedMask;
            else cam.cullingMask &= ~combinedMask;

            OnRoomGeometryRenderChanged?.Invoke(render);
        }

        /// <summary>
        /// Toggles the visibility of the scene while a loading process is in progress.
        /// </summary>
        public void HideSceneWhileLoading(bool hide)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            cam.cullingMask = hide ? LayerMask.GetMask("MUES_RenderWhileLoading") : originalCullingMask | LayerMask.GetMask("MUES_Floor");

            if (hide) OnLoadingStarted?.Invoke();
            else OnLoadingEnded?.Invoke();
        }

        /// <summary>
        /// Sends the captured room data to other clients.
        /// </summary>
        public void SendRoomDataTo(PlayerRef player)
        {
            if (!Net.Runner.IsSharedModeMasterClient)
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
        /// Teleports the local OVRCameraRig to the first available chair in the scene. (REMOTE CLIENT ONLY)
        /// </summary>
        public IEnumerator TeleportToFirstFreeChair()
        {
            if (Net == null || !Net.isRemote)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] TeleportToFirstFreeChair skipped - not a remote client.", Color.yellow);
                OnTeleportCompleted?.Invoke();
                yield break;
            }

            yield return WaitForConditionWithTimeout(() => Net.isConnected, DefaultTimeout);

            yield return WaitForChairsInScene();

            if (chairsInScene.Count == 0)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] No chairs in scene to teleport to.", Color.yellow);
                OnTeleportCompleted?.Invoke();
                yield break;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Looking for free chair among {chairsInScene.Count} chairs...", Color.cyan);

            var targetChair = chairsInScene.FirstOrDefault(c => c != null && !c.IsOccupied)?.transform;
            Vector3 targetPosition = GetTeleportTargetPosition(targetChair);

            TeleportCameraRig(targetPosition);

            yield return CreateRemoteLocalAnchor();
            OnTeleportCompleted?.Invoke();
        }

        #endregion

        #region Scene Mesh Data Serialization - Capture

        /// <summary>
        /// Coroutine to capture the scene. (HOST ONLY)
        /// </summary>
        private IEnumerator CaptureRoomRoutine()
        {
            yield return new WaitForEndOfFrame();

            if (Net != null && Net.useCustomRoomModel)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] CaptureRoomRoutine: useCustomRoomModel enabled, creating invisible floor for chair placement.", Color.cyan);
                CreateInvisibleFloorForCustomRoom();
                
                Net.InvokeOnCustomRoomInit();
                
                yield return SwitchToChairPlacement(true);
                yield break;
            }

            MRUKRoom room = GetActiveRoom();
            if (room == null)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] No MRUKRoom found in scene! Can't capture room!", Color.red);
                Net.LeaveRoom();
                yield break;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Capturing room: {room.name} with {room.Anchors.Count} anchors.", Color.cyan);
            room.transform.localScale = Vector3.one;

            yield return null;

            Transform referenceTransform = Net?.sceneParent ?? room.transform;
            LogReferenceTransform(referenceTransform);

            currentRoomData = BuildRoomData(room.Anchors, referenceTransform);
            CaptureTableTransforms();

            Transform floorTransform = GetFloorTransform(room.Anchors);
            if (floorTransform == null)
            {
                Net.LeaveRoom();
                yield break;
            }

            if (!SetupFloor(floorTransform))
            {
                Net.LeaveRoom();
                yield break;
            }

            Destroy(room.gameObject);

            yield return new WaitForSeconds(0.1f);
            yield return SwitchToChairPlacement(true);
        }

        /// <summary>
        /// Retrieves the active MRUKRoom from the networking instance or by finding it in the scene.
        /// </summary>
        private MRUKRoom GetActiveRoom()
        {
            MRUKRoom room = Net?.activeRoom;
            if (room == null)
            {
                room = FindFirstObjectByType<MRUKRoom>();
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Warning: activeRoom was null, falling back to FindFirstObjectByType.", Color.yellow);
            }
            return room;
        }

        /// <summary>
        /// Logs information about the reference transform used for room capture.
        /// </summary>
        private void LogReferenceTransform(Transform referenceTransform)
        {
            if (Net?.sceneParent != null)
                Debug.Log($"[MUES_RoomVisualizer] Capturing room relative to SceneParent: {referenceTransform.name}");
            else
                Debug.LogWarning("[MUES_RoomVisualizer] Capturing room relative to Room transform (SceneParent not found), this may cause misalignment.");
        }

        /// <summary>
        /// Builds room data from the given anchors and reference transform.
        /// </summary>
        private RoomData BuildRoomData(List<MRUKAnchor> anchors, Transform referenceTransform)
        {
            var anchorTransformDataList = new List<AnchorTransformData>(anchors.Count);
            var floorCeilingData = new FloorCeilingData();

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

                CaptureFloorCeilingMesh(anchor, prefab, floorCeilingData);
                anchorTransformDataList.Add(entry);
            }

            return new RoomData
            {
                anchorTransformData = anchorTransformDataList.ToArray(),
                floorCeilingData = floorCeilingData
            };
        }

        /// <summary>
        /// Captures mesh data for floor and ceiling anchors.
        /// </summary>
        private void CaptureFloorCeilingMesh(MRUKAnchor anchor, Transform prefab, FloorCeilingData floorCeilingData)
        {
            if (anchor.name != "FLOOR" && anchor.name != "CEILING") return;

            var mf = prefab.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null) return;

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
                floorCeilingData.floorVertices = vertexArray;
                floorCeilingData.floorTriangles = mesh.triangles;
                floorHeight = anchor.transform.position.y;
            }
            else
            {
                floorCeilingData.ceilingVertices = vertexArray;
                floorCeilingData.ceilingTriangles = mesh.triangles;
            }
        }

        /// <summary>
        /// Captures table transforms from the scene and parents them to the scene parent.
        /// </summary>
        private void CaptureTableTransforms()
        {
            currentTableTransforms.Clear();

            var tableAnchors = FindObjectsByType<MRUKAnchor>(FindObjectsSortMode.None)
                .Where(a => a.Label == MRUKAnchor.SceneLabels.TABLE)
                .Select(a => a.transform)
                .ToList();

            foreach (var table in tableAnchors)
            {
                if (currentTableTransforms.Contains(table)) continue;

                currentTableTransforms.Add(table);

                if (table.TryGetComponent<MRUKAnchor>(out var anchor))
                    anchor.enabled = false;

                table.SetParent(Net.sceneParent, true);
                table.localScale = Vector3.zero;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Found {currentTableTransforms.Count} tables", Color.cyan);
        }

        /// <summary>
        /// Retrieves the floor transform from the given anchors.
        /// </summary>
        private Transform GetFloorTransform(List<MRUKAnchor> anchors)
        {
            Transform floorTransform = MUES_Networking.GetRoomCenter();

            if (floorTransform == null)
            {
                var floorAnchor = anchors.FirstOrDefault(a => a.name == "FLOOR");
                if (floorAnchor != null)
                {
                    floorTransform = floorAnchor.transform;
                    ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Using FLOOR anchor from room as fallback.", Color.yellow);
                }
                else
                {
                    Debug.LogError("[MUES_RoomVisualizer] CaptureRoomRoutine: Room center transform is null and no FLOOR anchor found!");
                    return null;
                }
            }

            if (floorTransform.childCount == 0)
            {
                if (Net != null && Net.useCustomRoomModel)
                {
                    ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] FLOOR has no child objects (custom room model mode).", Color.yellow);
                    return null;
                }
                
                Debug.LogError("[MUES_RoomVisualizer] CaptureRoomRoutine: FLOOR has no child objects!");
                return null;
            }

            return floorTransform;
        }

        /// <summary>
        /// Creates an invisible floor plane for chair placement when using custom room models.
        /// </summary>
        private void CreateInvisibleFloorForCustomRoom() => CreateInvisibleFloor(Net?.sceneParent);

        /// <summary>
        /// Creates an invisible floor plane with the specified parent transform.
        /// </summary>
        private void CreateInvisibleFloor(Transform parentTransform)
        {
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            float floorY = rig != null ? rig.trackingSpace.position.y : 0f;
            floorHeight = floorY;

            GameObject floorParent = new GameObject("FLOOR");
            if (parentTransform != null)
            {
                floorParent.transform.SetParent(parentTransform, false);
                floorParent.transform.localPosition = Vector3.zero;
                floorParent.transform.localRotation = Quaternion.identity;
            }
            else
                floorParent.transform.position = new Vector3(0, floorY, 0);

            GameObject invisibleFloor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            invisibleFloor.name = "InvisibleFloorPlane";
            invisibleFloor.transform.SetParent(floorParent.transform, false);
            invisibleFloor.transform.localPosition = Vector3.zero;
            invisibleFloor.transform.localRotation = Quaternion.identity;
            invisibleFloor.transform.localScale = new Vector3(customRoomFloorSize.x * 0.1f, 1f, customRoomFloorSize.y * 0.1f); // Plane is 10x10 by default

            var meshRenderer = invisibleFloor.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                Destroy(meshRenderer);

            var collider = invisibleFloor.GetComponent<MeshCollider>();
            if (collider == null)
                collider = invisibleFloor.AddComponent<MeshCollider>();

            invisibleFloor.layer = LayerMask.NameToLayer("MUES_Floor");

            var rb = invisibleFloor.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            floor = invisibleFloor;

            ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Created invisible floor at Y={floorY} with size {customRoomFloorSize}", Color.green);
        }

        /// <summary>
        /// Sets up the floor object with proper components and parenting.
        /// </summary>
        private bool SetupFloor(Transform floorTransform)
        {
            floor = floorTransform.GetChild(0).gameObject;

            var floorRenderer = floor.transform.GetComponent<Renderer>();
            if (floorRenderer != null)
                floorRenderer.enabled = false;

            if (floorTransform.TryGetComponent<MRUKAnchor>(out var floorMRUKAnchor))
                floorMRUKAnchor.enabled = false;

            floor.transform.parent.SetParent(Net.sceneParent, true);

            if (!floor.TryGetComponent<Rigidbody>(out _))
            {
                var rb = floor.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            floor.layer = LayerMask.NameToLayer("MUES_Floor");
            return true;
        }

        /// <summary>
        /// Finalizes the room data by capturing chair transformations. (HOST ONLY)
        /// </summary>
        private void FinalizeRoomData()
        {
            foreach (var item in chairsInScene)
                item.GetComponent<MUES_AnchoredNetworkBehaviour>().ForceUpdateAnchorOffset();

            StartCoroutine(SwitchToChairPlacement(false));
            Net.EnableJoining();
        }

        /// <summary>
        /// Returns an integer type based on the anchor's label. (HOST ONLY)
        /// </summary>
        private int GetTypeFromLabel(MRUKAnchor anchor)
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
            if (!enabled)
            {
                chairPlacement = false;
                OnChairPlacementEnded?.Invoke();
            }
            else
            {
                yield return null;
                previewChair = Instantiate(chairPrefab);
                RenderRoomGeometry(true);
                OnChairPlacementStarted?.Invoke();
            }

            yield return AnimateTables(enabled);

            if (enabled)
            {
                chairPlacement = true;
            }
            else
            {
                Destroy(previewChair);
                
                if (floor != null && floor.transform.parent != null)
                {
                    if (Net != null && Net.useCustomRoomModel)
                        floor.transform.parent.gameObject.SetActive(false);
                    else
                        Destroy(floor.transform.parent.gameObject);
                }

                foreach (var table in currentTableTransforms)
                    Destroy(table.gameObject);

                currentTableTransforms.Clear();
            }
        }

        /// <summary>
        /// Animates table scaling during chair placement mode transitions.
        /// </summary>
        private IEnumerator AnimateTables(bool enabled)
        {
            Sequence seq = DOTween.Sequence();
            foreach (var table in currentTableTransforms)
            {
                if (table == null) continue;

                if (table.TryGetComponent<ScalableTableComponent>(out var scalableTable))
                    Destroy(scalableTable);

                seq.Join(table.DOScale(enabled ? Vector3.one : Vector3.zero, .35f).SetEase(Ease.OutExpo));
            }

            yield return seq.WaitForCompletion();
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

                bool spawnComplete = false;
                yield return WaitForConditionWithTimeout(() => spawnedObj != null, 1f, () => spawnComplete = false);
                spawnComplete = spawnedObj != null;

                if (!spawnComplete)
                {
                    Debug.LogWarning("[MUES_RoomVisualizer] Chair spawn timed out or failed.");
                    yield break;
                }

                if (spawnedObj.TryGetComponent<MUES_Chair>(out var existingChair))
                    chairsInScene.Add(existingChair);

                spawnedObj.transform.localScale = Vector3.zero;
                ConfigureChairConstraints(spawnedObj);

                yield return spawnedObj.transform.DOScale(targetScale, 0.3f).SetEase(Ease.OutExpo).WaitForCompletion();
                OnChairPlaced?.Invoke(spawnedObj.transform);
            }
            finally
            {
                chairCount++;
                chairAnimInProgress = false;
            }
        }

        /// <summary>
        /// Configures position constraints for a placed chair.
        /// </summary>
        private void ConfigureChairConstraints(MUES_NetworkedTransform spawnedObj)
        {
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
        /// Sets the floor height from current room data.
        /// </summary>
        private void SetFloorHeight()
        {
            foreach (var data in currentRoomData.anchorTransformData)
            {
                if (data.name == "FLOOR")
                {
                    floorHeight = virtualRoom.transform.TransformPoint(data.anchorTransform.ToPosition()).y;
                    break;
                }
            }
        }

        /// <summary>
        /// Parents the virtual room to the remote scene parent. Only applicable for remote clients.
        /// </summary>
        private void ParentVirtualRoom()
        {
            if (Net == null)
            {
                virtualRoom.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] ParentVirtualRoom: Networking null. Placing at origin.", Color.red);
                return;
            }

            remoteSceneParent = GetOrCreateRemoteSceneParent();
            remoteSceneParent.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            virtualRoom.transform.SetParent(remoteSceneParent, false);
            virtualRoom.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            virtualRoom.transform.localScale = Vector3.one;

            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] ParentVirtualRoom: Room parented to REMOTE_SCENE_PARENT at origin, will be anchored after teleport.", Color.cyan);
        }

        /// <summary>
        /// Instantiates all anchors from the current room data.
        /// </summary>
        private void InstantiateAnchors()
        {
            foreach (var data in currentRoomData.anchorTransformData)
            {
                GameObject anchorInstance = new(data.name);
                anchorInstance.transform.SetParent(virtualRoom.transform);
                anchorInstance.transform.SetLocalPositionAndRotation(data.anchorTransform.ToPosition(), data.anchorTransform.ToRotation());
                anchorInstance.transform.localScale = data.anchorTransform.ToScale();

                GameObject prefabInstance = CreatePrefabInstance(data, anchorInstance.transform);
                prefabInstance.transform.SetLocalPositionAndRotation(data.prefabTransform.ToPosition(), data.prefabTransform.ToRotation());
                prefabInstance.transform.localScale = data.prefabTransform.ToScale();

                if (data.type == -1)
                    SetupFloorCeilingPrefab(data, prefabInstance);

                instantiatedRoomPrefabs.Add(anchorInstance.transform);
            }
        }

        /// <summary>
        /// Creates a prefab instance for the given anchor data.
        /// </summary>
        private GameObject CreatePrefabInstance(AnchorTransformData data, Transform parent)
        {
            if (data.type >= 0)
                return Instantiate(roomPrefabs[data.type], parent);

            GameObject prefabInstance = new GameObject();
            prefabInstance.transform.SetParent(parent);
            return prefabInstance;
        }

        /// <summary>
        /// Sets up floor or ceiling prefab with mesh and materials.
        /// </summary>
        private void SetupFloorCeilingPrefab(AnchorTransformData data, GameObject prefabInstance)
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
                prefabInstance.layer = LayerMask.NameToLayer("MUES_Floor");
        }

        /// <summary>
        /// Finds or creates the REMOTE_SCENE_PARENT GameObject for remote clients.
        /// </summary>
        private Transform GetOrCreateRemoteSceneParent()
        {
            GameObject parent = GameObject.Find("REMOTE_SCENE_PARENT") ?? new GameObject("REMOTE_SCENE_PARENT");
            return parent.transform;
        }

        /// <summary>
        /// Waits for chairs to appear in the scene within the timeout period.
        /// </summary>
        private IEnumerator WaitForChairsInScene()
        {
            float elapsed = 0f;

            while (chairsInScene.Count == 0 && elapsed < DefaultTimeout)
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
        }

        /// <summary>
        /// Gets the teleport target position from a chair or room center.
        /// </summary>
        private Vector3 GetTeleportTargetPosition(Transform targetChair)
        {
            if (targetChair != null)
            {
                ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Teleporting to chair at position {targetChair.position}.", Color.green);
                return targetChair.position;
            }

            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] No free chair found, teleporting to room center and activating teleport surface.", Color.yellow);
            Transform roomCenter = MUES_Networking.GetRoomCenter();
            Vector3 targetPosition = roomCenter != null ? roomCenter.position : Vector3.zero;

            if (roomCenter != null)
                Instantiate(teleportSurface, targetPosition, Quaternion.identity, roomCenter);

            return targetPosition;
        }

        /// <summary>
        /// Teleports the camera rig to the specified position with offset compensation.
        /// </summary>
        private void TeleportCameraRig(Vector3 targetPosition)
        {
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
            {
                ovrManager.transform.SetPositionAndRotation(targetPosition, ovrManager.transform.rotation);
            }
        }

        /// <summary>
        /// Creates a local OVRSpatialAnchor for remote clients to stabilize the scene parent position against HMD recentering.
        /// </summary>
        private IEnumerator CreateRemoteLocalAnchor()
        {
            if (Net == null || !Net.isRemote || remoteSceneParent == null)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] CreateRemoteLocalAnchor skipped - not applicable.", Color.yellow);
                yield break;
            }

            Vector3 anchorPosition = remoteSceneParent.position;
            Quaternion anchorRotation = remoteSceneParent.rotation;

            GameObject anchorGO = new GameObject("RemoteLocalAnchor");
            anchorGO.transform.SetPositionAndRotation(anchorPosition, anchorRotation);

            _remoteLocalAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();

            bool anchorCreated = false;
            yield return WaitForConditionWithTimeout(
                () => _remoteLocalAnchor != null && _remoteLocalAnchor.Created,
                DefaultTimeout,
                () => anchorCreated = false
            );
            
            if (_remoteLocalAnchor == null)
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Remote local anchor was destroyed during creation.", Color.yellow);
                if (anchorGO != null) Destroy(anchorGO);
                yield break;
            }
            
            anchorCreated = _remoteLocalAnchor.Created;

            if (anchorCreated)
            {
                _remoteStabilizer.Initialize(anchorPosition, anchorRotation);
                ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Remote local anchor created at {anchorPosition}. REMOTE_SCENE_PARENT will now be stabilized (mirrors colocated behavior).", Color.green);
            }
            else
            {
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Failed to create remote local anchor - scene may drift on recenter.", Color.yellow);
                Destroy(anchorGO);
                _remoteLocalAnchor = null;
                Net?.LeaveRoom();
            }
        }

        /// <summary>
        /// Updates the remote scene parent position for remote clients based on the local spatial anchor.
        /// </summary>
        private void UpdateRemoteSceneParent()
        {
            if (Net == null || !Net.isRemote || remoteSceneParent == null || !_remoteStabilizer.IsInitialized)
                return;

            if (_remoteLocalAnchor == null || !_remoteLocalAnchor.Localized)
                return;

            _remoteStabilizer.UpdateSceneParent(remoteSceneParent, _remoteLocalAnchor.transform, debugMode, "[MUES_RoomVisualizer]");
        }

        #endregion

        #region Networking Methods

        /// <summary>
        /// Receives room data for a specific player and instantiates the geometry.
        /// </summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void RPC_ReceiveRoomDataForPlayer([RpcTarget] PlayerRef targetPlayer, string json, RpcInfo info = default) => SetRoomDataFromJson(json);

        #endregion

        #region Scene Visualizer Methods

        /// <summary>
        /// Instantiates loading visuals.
        /// </summary>
        private void InitializeVisuals()
        {
            Transform floorTransform = virtualRoom?.transform.Find("FLOOR") ?? GameObject.Find("FLOOR")?.transform;

            if (floorTransform != null)
                SetupParticleSystem(floorTransform);
            else
                ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] InitializeVisuals: Could not find FLOOR anchor! Particle effects skipped.", Color.yellow);

            foreach (var anchor in instantiatedRoomPrefabs)
                if (anchor != null) anchor.localScale = Vector3.zero;

            sceneShown = false;
            StartCoroutine(TeleportToFirstFreeChair());
        }

        /// <summary>
        /// Sets up the particle system for room visualization.
        /// </summary>
        private void SetupParticleSystem(Transform floorTransform)
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

        #endregion

        /// <summary>
        /// Waits for a condition to become true within the specified timeout period.
        /// </summary>
        private IEnumerator WaitForConditionWithTimeout(Func<bool> condition, float timeout = DefaultTimeout, Action onTimeout = null)
        {
            float elapsed = 0f;
            while (!condition() && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (elapsed >= timeout)
                onTimeout?.Invoke();
        }
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

        /// <summary>
        /// Converts the stored position data to a Vector3.
        /// </summary>
        public Vector3 ToPosition() => new(localPosition[0], localPosition[1], localPosition[2]);

        /// <summary>
        /// Converts the stored rotation data to a Quaternion.
        /// </summary>
        public Quaternion ToRotation() => new(localRotation[0], localRotation[1], localRotation[2], localRotation[3]);

        /// <summary>
        /// Converts the stored scale data to a Vector3.
        /// </summary>
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

        /// <summary>
        /// Creates a mesh from vertex data and triangles.
        /// </summary>
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
}