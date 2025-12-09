using DG.Tweening;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using static OVRInput;
using System.IO;
using static Oculus.Interaction.TransformerUtils;
using Fusion;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MUES_RoomVisualizer : NetworkBehaviour
{
    [Header("General:")]
    [Tooltip("Button to save the room after placement.")]
    public Button saveRoomButton = Button.Two;
    [Tooltip("Button to place a chair in the room.")]
    public Button placeChairButton = Button.PrimaryIndexTrigger;
    [Tooltip("Data to load the room from.")]
    [Optional] public string roomDataPath;

    [Header("Object References:")]
    [Tooltip("Prefab for loading particles.")]
    public GameObject roomParticlesPrefab;
    [Tooltip("Material for floor and ceiling.")]
    public Material floorCeilingMat;
    [Tooltip("Prefab for chair placement visualization.")]
    public GameObject chairPrefab;
    [Tooltip("Networked prefab for chairs.")]
    public NetworkObject networkedChairPrefab;
    [Tooltip("Layer mask for floor raycasting during chair placement.")]
    public LayerMask floorLayer;

    [Header("Debug:")]
    [Tooltip("Enables debug mode, preventing capturing the room.")]
    public bool debugMode = false;

    [HideInInspector] public bool HasRoomData => currentRoomData != null; // Indicates if room data is available.

    private ParticleSystem _particleSystem; // Reference to the ParticleSystem component.
    private ParticleSystemRenderer _particleSystemRenderer; // Reference to the ParticleSystemRenderer component.
    private AnchorPrefabSpawner prefabSpawner;  // Reference to the AnchorPrefabSpawner component.

    private RoomData currentRoomData;  // Data structure to hold captured room data.
    private List<Transform> instantiatedRoomPrefabs = new();    // List to store instantiated room prefabs.

    private List<Transform> currentChairTransforms = new(); // List to store chair transforms.
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
        if(Instance == null) 
            Instance = this;

        ImmersiveSceneDebugger debugger = FindFirstObjectByType<ImmersiveSceneDebugger>();

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

        foreach (var prefab in prefabSpawner.PrefabsToSpawn)
            roomPrefabs.Add(prefab.Prefabs[0]);

        if (debugMode)
        {
            LoadRoomDataFromFile(roomDataPath);
            InstantiateRoomGeometry();
        }
    }

    private void Update()
    {
        if (chairPlacement && previewChair != null)
        {
            Ray ray = new(rightController.transform.position, rightController.transform.forward);
            bool rayHit = Physics.Raycast(ray, out RaycastHit hitInfo, 10, floorLayer);

            previewChair.SetActive(rayHit);

            if (GetDown(saveRoomButton)) FinalizeRoomData();
            if (GetDown(placeChairButton) && rayHit && !chairAnimInProgress) StartCoroutine(PlaceChair(hitInfo.point, previewChair.transform.localScale));

            if (previewChair.activeSelf)
            {
                Vector3 smoothedTargetPosition = Vector3.Lerp(previewChair.transform.position, hitInfo.point, Time.deltaTime * 15);
                previewChair.transform.SetPositionAndRotation(smoothedTargetPosition, GetRotationTowardsNearestTable(smoothedTargetPosition));
            }
        }
    }

    private void OnValidate()   // sanitize loading path
    {
        if (string.IsNullOrEmpty(roomDataPath)) return;
        roomDataPath = roomDataPath.Trim('"');
        roomDataPath = roomDataPath.Replace("\\", "/");

        if (!roomDataPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            roomDataPath += ".bin";
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
        if (room == null) yield break;

       room.transform.localScale = Vector3.one;

        var anchors = room.Anchors;
        var anchorTransformDataList = new List<AnchorTransformData>(anchors.Count);
        var _floorCeilingData = new FloorCeilingData();

        foreach (var anchor in anchors)
        {
            if (anchor == null || anchor.transform == null || anchor.transform.childCount == 0) continue;

            var anchorData = new TransformationData(
            anchor.transform.localPosition,
            anchor.transform.localRotation,
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
                if (mf != null && mf.sharedMesh != null)
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
                    {
                        Vector3 pos = verts[i];
                        Vector3 n = hasNorms ? norms[i] : Vector3.up;
                        Vector2 uv = hasUvs ? uvs[i] : Vector2.zero;

                        vertexArray[i] = new VertexData(pos, n, uv);
                    }

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

        currentTableTransforms = FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == "TABLE").Select(t => t.transform).ToList();

        foreach (var table in currentTableTransforms)
        {
            table.SetParent(null);
            table.GetComponent<MRUKAnchor>().enabled = false;
            table.localScale = Vector3.zero;
        }

        floor = MUES_Networking.GetRoomCenter().GetChild(0).gameObject;
        floor.transform.parent.SetParent(null);

        floor.GetComponent<Renderer>().enabled = false;
        floor.transform.parent.GetComponent<MRUKAnchor>().enabled = false;

        Rigidbody rb = floor.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        floor.layer = LayerMask.NameToLayer("Floor");

        Destroy(room.gameObject);
        yield return SwitchToChairPlacement(room);
    }

    /// <summary>
    /// Finalizes the room data by capturing chair transformations. (HOST ONLY)
    /// </summary>
    private void FinalizeRoomData()
    {
        List<TransformationData> chairDataList = new();

        foreach (var item in currentChairTransforms)
        {
            TransformationData data = new TransformationData(item.localPosition, item.localRotation, item.localScale);
            chairDataList.Add(data);
        }

        ChairData _chairData = new ChairData { chairTransforms = chairDataList.ToArray() };

        StartCoroutine(SwitchToChairPlacement(false));

        string dir = Path.Combine(Application.persistentDataPath, "Rooms");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"RoomData_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(1);
                bw.Write(currentRoomData.anchorTransformData.Length);
                foreach (var entry in currentRoomData.anchorTransformData)
                {
                    bw.Write(entry.name);
                    bw.Write(entry.type);

                    WriteTransformation(bw, entry.anchorTransform);
                    WriteTransformation(bw, entry.prefabTransform);
                }

                WriteVertexArray(bw, currentRoomData.floorCeilingData.floorVertices);
                WriteIntArray(bw, currentRoomData.floorCeilingData.floorTriangles);

                WriteVertexArray(bw, currentRoomData.floorCeilingData.ceilingVertices);
                WriteIntArray(bw, currentRoomData.floorCeilingData.ceilingTriangles);

                WriteChairData(bw, _chairData);
            }

            Debug.Log($"<color=lime>[MUES_CubicRoomVisualizer] Saved room to: {path}</color>");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MUES_CubicRoomVisualizer] Failed to save room: {ex}");
            return;
        }

        currentChairTransforms.Clear();
        currentTableTransforms.Clear();

        MUES_Networking.Instance.EnableJoining();
    }

    /// <summary>
    /// Writes transformation data to a binary writer. (HOST ONLY)
    /// </summary>
    private void WriteTransformation(BinaryWriter bw, TransformationData t)
    {
        bw.Write(t.localPosition[0]); bw.Write(t.localPosition[1]); bw.Write(t.localPosition[2]);
        bw.Write(t.localRotation[0]); bw.Write(t.localRotation[1]); bw.Write(t.localRotation[2]); bw.Write(t.localRotation[3]);
        bw.Write(t.localScale[0]); bw.Write(t.localScale[1]); bw.Write(t.localScale[2]);
    }

    /// <summary>
    /// Writes an integer array to a binary writer. (HOST ONLY)
    /// </summary>
    private void WriteIntArray(BinaryWriter bw, int[] arr)
    {
        if (arr == null || arr.Length == 0)
        {
            bw.Write(0);
            return;
        }

        bw.Write(arr.Length);

        for (int i = 0; i < arr.Length; i++)
            bw.Write(arr[i]);
    }

    /// <summary>
    /// Reads an integer array from a binary reader. (HOST ONLY)
    /// </summary>
    private int[] ReadIntArray(BinaryReader br)
    {
        int count = br.ReadInt32();
        if (count <= 0) return null;

        int[] arr = new int[count];
        for (int i = 0; i < count; i++)
            arr[i] = br.ReadInt32();

        return arr;
    }

    /// <summary>
    /// Writes chair data to a binary writer. (HOST ONLY)
    /// </summary>
    private void WriteVertexArray(BinaryWriter bw, VertexData[] vertices)
    {
        if (vertices == null || vertices.Length == 0)
        {
            bw.Write(0);
            return;
        }

        bw.Write(vertices.Length);

        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];

            bw.Write(v.position[0]);
            bw.Write(v.position[1]);
            bw.Write(v.position[2]);

            bw.Write(v.normal[0]);
            bw.Write(v.normal[1]);
            bw.Write(v.normal[2]);

            bw.Write(v.uv[0]);
            bw.Write(v.uv[1]);
        }
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
        else previewChair = Instantiate(chairPrefab);

        Sequence seq = DOTween.Sequence();
        foreach (var table in currentTableTransforms)
        {
            if (table == null) continue;
            DG.Tweening.Tween scaleTween = table.DOScale(enabled ? Vector3.one : Vector3.zero, .35f).SetEase(Ease.OutExpo);

            seq.Join(scaleTween);
        }

        yield return seq.WaitForCompletion();

        if (enabled) chairPlacement = true;
        else
        {
            Destroy(previewChair);  
            Destroy(floor.transform.parent.gameObject);

            foreach (var table in currentTableTransforms)
                Destroy(table.gameObject);
        }
    }

    /// <summary>
    /// Places a chair at the specified position with animation. (HOST ONLY)
    /// </summary>
    private IEnumerator PlaceChair(Vector3 position, Vector3 targetScale, Quaternion? rotation = null)
    {
        chairAnimInProgress = true;

        Transform t = Runner.Spawn(networkedChairPrefab, position, Quaternion.identity).transform;
        currentChairTransforms.Add(t);
        t.localScale = Vector3.zero;

        Quaternion finalRot = rotation ?? GetRotationTowardsNearestTable(position);
        t.rotation = finalRot;

        GrabFreeTransformer gft = t.GetComponent<GrabFreeTransformer>();

        var posConstraints = new PositionConstraints
        {
            ConstraintsAreRelative = false,

            XAxis = ConstrainedAxis.Unconstrained,
            ZAxis = ConstrainedAxis.Unconstrained,

            YAxis = new ConstrainedAxis
            {
                ConstrainAxis = true,
                AxisRange = new FloatRange
                {
                    Min = t.position.y,
                    Max = t.position.y
                }
            }
        };

        gft.InjectOptionalPositionConstraints(posConstraints);

        DG.Tweening.Tween scaleTween = t.DOScale(targetScale, 0.3f).SetEase(Ease.OutExpo);
        yield return scaleTween.WaitForCompletion();

        chairAnimInProgress = false;
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

        dir.Normalize();
        return Quaternion.LookRotation(dir, Vector3.up);
    }

    /// <summary> 
    /// Writes chair data to a binary writer. (HOST ONLY)
    /// </summary>
    private void WriteChairData(BinaryWriter bw, ChairData chairData)
    {
        if (chairData == null || chairData.chairTransforms == null)
        {
            bw.Write(0);
            return;
        }

        bw.Write(chairData.chairTransforms.Length);
        foreach (var t in chairData.chairTransforms)
            WriteTransformation(bw, t);
    }

    /// <summary>
    /// Reads chair data from a binary reader. (HOST ONLY)
    /// </summary>
    private ChairData ReadChairData(BinaryReader br)
    {
        int count = br.ReadInt32();
        var data = new ChairData();

        if (count <= 0)
        {
            data.chairTransforms = Array.Empty<TransformationData>();
            return data;
        }

        var list = new TransformationData[count];
        for (int i = 0; i < count; i++)
        {
            list[i] = ReadTransformation(br);
        }

        data.chairTransforms = list;
        return data;
    }

    #endregion

    #region Scene Mesh Data Serialization - Place

    /// <summary>
    /// Reads room data from a binary file for sending. (HOST ONLY)
    /// </summary>
    public void LoadRoomDataFromFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogError("[MUES_RoomVisualizer] LoadRoomDataFromFile: filePath is null or empty.");
            return;
        }
        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                int fileVersion = br.ReadInt32();

                int anchorCount = br.ReadInt32();
                var anchorList = new List<AnchorTransformData>(anchorCount);

                for (int i = 0; i < anchorCount; i++)
                {
                    var entry = new AnchorTransformData();
                    entry.name = br.ReadString();
                    entry.type = br.ReadInt32();
                    entry.anchorTransform = ReadTransformation(br);
                    entry.prefabTransform = ReadTransformation(br);

                    anchorList.Add(entry);
                }

                VertexData[] floorVerts = ReadVertexArray(br);
                int[] floorTris = ReadIntArray(br);

                VertexData[] ceilingVerts = ReadVertexArray(br);
                int[] ceilingTris = ReadIntArray(br);

                ChairData chairData = ReadChairData(br);

                var floorCeiling = new FloorCeilingData
                {
                    floorVertices = floorVerts,
                    floorTriangles = floorTris,

                    ceilingVertices = ceilingVerts,
                    ceilingTriangles = ceilingTris
                };

                var roomData = new RoomData
                {
                    anchorTransformData = anchorList.ToArray(),
                    floorCeilingData = floorCeiling
                };

                foreach (var t in chairData.chairTransforms)
                    StartCoroutine(PlaceChair(t.ToPosition(), t.ToScale(), t.ToRotation()));

                currentRoomData = roomData;

                Debug.Log($"<color=lime>[MUES_RoomVisualizer] LoadRoomDataFromFile: Serialized room data from file: {filePath}</color>");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MUES_RoomVisualizer] LoadRoomDataFromFile: Failed to read room data: {ex}");
            return;
        }

        MUES_Networking.Instance.EnableJoining();
    }

    /// <summary>
    /// Loads a room from room data. (REMOTE CLIENT ONLY)
    /// </summary>
    public void InstantiateRoomGeometry()
    {
        if (currentRoomData == null)
        {
            Debug.LogError($"[MUES_CubicRoomVisualizer] No data provided! Can't load room!");
            return;
        }

        foreach (var old in instantiatedRoomPrefabs)
            if (old != null && old.transform != null)
                Destroy(old.transform.gameObject);

        instantiatedRoomPrefabs.Clear();

        GameObject newRoom = new("InstantiatedRoom");
        newRoom.transform.position = new(Camera.main.transform.position.x, 0, Camera.main.transform.position.z);

        Vector3 floorPosition = Vector3.one;
        Quaternion floorRotation = Quaternion.identity;

        foreach (var data in currentRoomData.anchorTransformData)
        {
            GameObject anchorInstance = new(data.name);
            anchorInstance.transform.SetParent(newRoom.transform);

            anchorInstance.transform.SetLocalPositionAndRotation(data.anchorTransform.ToPosition(), data.anchorTransform.ToRotation());
            anchorInstance.transform.localScale = data.anchorTransform.ToScale();

            GameObject prefabInstance;

            if (data.type >= 0) prefabInstance = Instantiate(roomPrefabs[data.type], anchorInstance.transform);
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
                if (isFloor)
                {
                    floorHeight = anchorInstance.transform.position.y;
                    anchorInstance.transform.GetPositionAndRotation(out floorPosition, out floorRotation);
                }

                MeshFilter mf = prefabInstance.AddComponent<MeshFilter>();
                var mesh = VertexData.CreateMeshFromVertexData(vertexDataArray, tris);
                mesh.triangles = isFloor? currentRoomData.floorCeilingData.floorTriangles: currentRoomData.floorCeilingData.ceilingTriangles;

                mf.sharedMesh = mesh;
                mf.sharedMesh.name = isFloor ? "FloorMesh" : "CeilingMesh";

                MeshRenderer mr = prefabInstance.AddComponent<MeshRenderer>();
                mr.material = floorCeilingMat;

                prefabInstance.AddComponent<MeshCollider>();
            }

            instantiatedRoomPrefabs.Add(anchorInstance.transform);
        }

        OVRManager.instance.transform.SetPositionAndRotation(floorPosition, floorRotation);
        Camera.main.transform.position = floorPosition + new Vector3(0, Camera.main.transform.position.y, 0);

        Debug.Log($"<color=lime>[MUES_CubicRoomVisualizer] Instantiated {instantiatedRoomPrefabs.Count} anchors from room data.</color>");

        InitializeVisuals();
    }

    /// <summary>
    /// Reads transformation data from a binary reader.
    /// </summary>
    private TransformationData ReadTransformation(BinaryReader br)
    {
        Vector3 pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        Quaternion rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        Vector3 scale = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        return new TransformationData(pos, rot, scale);
    }

    /// <summary>
    /// Reads a VertexData array from a binary reader.
    /// </summary>
    private VertexData[] ReadVertexArray(BinaryReader br)
    {
        int count = br.ReadInt32();
        if (count <= 0)
            return null;

        var result = new VertexData[count];

        for (int i = 0; i < count; i++)
        {
            Vector3 pos = new Vector3(
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle()
            );

            Vector3 normal = new Vector3(
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle()
            );

            Vector2 uv = new Vector2(
                br.ReadSingle(),
                br.ReadSingle()
            );

            result[i] = new VertexData(pos, normal, uv);
        }

        return result;
    }

    #endregion

    #region Networking Methods

    /// <summary>
    /// Sends the captured room data to other clients.
    /// </summary>
    public void SendRoomDataTo(PlayerRef player)
    {
        if (!Object.HasStateAuthority)
        {
            ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] SendRoomDataTo: no StateAuthority.", Color.red);
            return;
        }

        string json = JsonUtility.ToJson(currentRoomData);
        ConsoleMessage.Send(debugMode, $"[MUES_RoomVisualizer] Sending room data to player {player}...", Color.cyan);

        RPC_ReceiveRoomDataForPlayer(player, json);
    }

    /// <summary>
    /// Receives room data for a specific player and instantiates the geometry.
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_ReceiveRoomDataForPlayer([RpcTarget] PlayerRef targetPlayer, string json, RpcInfo info = default)
    {
        currentRoomData = JsonUtility.FromJson<RoomData>(json);

        ConsoleMessage.Send(debugMode, "[MUES_RoomVisualizer] Room data received, instantiating geometry...", Color.green);
        InstantiateRoomGeometry();
    }

    #endregion

    #region Visualizer Methods

    /// <summary>
    /// Instantiates loading visuals.
    /// </summary>
    private void InitializeVisuals()
    {
        Transform floorTransform = GameObject.Find("FLOOR").transform;

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
            emission.rateOverTime = firstChild.transform.localScale.magnitude; ;

            _particleSystem.Play();
        }

        foreach (var anchor in instantiatedRoomPrefabs)
            anchor.localScale = Vector3.zero;

        ToggleVisualization();
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
        if (isActive) _particleSystemRenderer.enabled = true;

        Sequence seq = DOTween.Sequence();
        foreach (var anchor in instantiatedRoomPrefabs)
        {
            if (anchor == null || anchor.transform == null) continue;

            DG.Tweening.Tween scaleTween = isActive
                ? anchor.transform.DOScale(Vector3.one, 0.5f).SetEase(Ease.OutExpo)
                : anchor.transform.DOScale(Vector3.zero, 0.5f).SetEase(Ease.InExpo);

            seq.Join(scaleTween);
        }

        yield return seq.WaitForCompletion();

        if (!isActive) _particleSystemRenderer.enabled = false;
    }

    #endregion
}

#region Data Classes

[Serializable]
public class InstantiatedRoomAnchor
{
    public Transform transform;
    public MeshRenderer meshRenderer;
}

[Serializable]
public class RoomData
{
    public AnchorTransformData[] anchorTransformData;
    public FloorCeilingData floorCeilingData;
}

[Serializable]
public class ChairData
{
    public TransformationData[] chairTransforms;
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

    public Vector3 ToPosition() => new Vector3(localPosition[0], localPosition[1], localPosition[2]);

    public Quaternion ToRotation() => new Quaternion(localRotation[0], localRotation[1], localRotation[2], localRotation[3]);

    public Vector3 ToScale() => new Vector3(localScale[0], localScale[1], localScale[2]);
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

        Mesh mesh = new Mesh();

        int vCount = vertexData.Length;
        Vector3[] verts = new Vector3[vCount];
        Vector3[] norms = new Vector3[vCount];
        Vector2[] uvs = new Vector2[vCount];

        for (int i = 0; i < vCount; i++)
        {
            var v = vertexData[i];

            verts[i] = new Vector3(
                v.position[0],
                v.position[1],
                v.position[2]);

            norms[i] = new Vector3(
                v.normal[0],
                v.normal[1],
                v.normal[2]);

            uvs[i] = new Vector2(
                v.uv[0],
                v.uv[1]);
        }

        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.uv = uvs;

        if (triangles != null && triangles.Length > 0)
            mesh.triangles = triangles;

        mesh.RecalculateBounds();
        mesh.RecalculateNormals();

        return mesh;
    }
}

#endregion