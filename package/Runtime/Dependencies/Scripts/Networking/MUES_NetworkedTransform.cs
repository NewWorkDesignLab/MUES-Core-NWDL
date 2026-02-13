using Fusion;
using Meta.XR.MultiplayerBlocks.Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using System;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using GLTFast;
using GLTFast.Materials;

#if USING_URP
using UnityEngine.Rendering.Universal;
#endif

#if UNITY_EDITOR
using UnityEditor;
using MUES.Core;
#endif

namespace MUES.Core
{
    public class MUES_NetworkedTransform : MUES_AnchoredNetworkBehaviour
    {
        [Header("Ownership Settings")]
        [Tooltip("When enabled, only the client who spawned this object can grab/control it. If the spawner leaves, the host takes over.")]
        public bool spawnerOnlyGrab = false;

        [HideInInspector][Networked] public NetworkBool IsGrabbable { get; set; }   // Indicates if the object is grabbable
        [HideInInspector][Networked] public NetworkBool SpawnerControlsTransform { get; set; }  // Indicates if the spawner controls the transform
        [HideInInspector][Networked] public int SpawnerPlayerId { get; set; } = -1; // PlayerId of the spawner
        [HideInInspector][Networked] public NetworkString<_64> ModelFileName { get; set; }  // Filename of the model to load

        [HideInInspector] public bool _isBeingGrabbed = false;   // Flag indicating if the object is currently being grabbed

        /// <summary>
        /// Event fired when the object has been fully initialized and is ready for interaction.
        /// </summary>
        public event Action OnInitialized;

        private GameObject activeRadial;   // Reference to the active loading radial instance
        private Grabbable _grabbable;   // Reference to the Grabbable component
        private GrabInteractable _grabInteractable; // Reference to the GrabInteractable component
        private HandGrabInteractable _handGrabInteractable; // Reference to the HandGrabInteractable component
        private TransferOwnershipFusion _ownershipTransfer;  // Reference to the TransferOwnershipFusion component

        private bool _modelLoaded = false;   // Flag indicating if the model has been loaded
        private bool _isGrabbableOnSpawn = false;   // Cache of initial grabbable state
        private int _lastKnownSpawnerPlayerId = -1; // Cache of last known spawner player ID
        private bool _lastKnownSpawnerControlsTransform = false;    // Cache of last known spawner controls transform state

        private Vector3 _lastLocalPosition; // Cache of last local position relative to reference transform
        private Quaternion _lastLocalRotation;  // Cache of last local rotation relative to reference transform
        private readonly float _movementThreshold = 0.001f;   // Threshold for position change detection
        private readonly float _rotationThreshold = 0.1f; // Threshold for rotation change detection

        private Vector3 _cachedScale = Vector3.one; // Cached original scale for visibility control

        /// <summary>
        /// Gets the reference transform for movement detection.
        /// </summary>
        private Transform ReferenceTransform => (Net == null || Net.isRemote) ? null : Net.sceneParent;

        /// <summary>
        /// Checks if the current client has authority over this object.
        /// </summary>
        private bool HasAuthority
        {
            get
            {
                try { return Object != null && Object.IsValid && (Object.HasInputAuthority || Object.HasStateAuthority); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Checks if the networked object is valid and accessible.
        /// </summary>
        private bool IsObjectValid => Object != null && Object.IsValid;

        /// <summary>
        /// Checks if the spawner is still connected to the session.
        /// </summary>
        private bool IsSpawnerConnected => Runner != null && Runner.ActivePlayers.Any(p => p.PlayerId == SpawnerPlayerId);

        /// <summary>
        /// Checks if the local player is allowed to control this object.
        /// </summary>
        private bool IsLocalPlayerAllowedToControl
        {
            get
            {
                if (!SpawnerControlsTransform || Runner == null) return true;
                if (SpawnerPlayerId == Runner.LocalPlayer.PlayerId) return true;

                try
                {
                    if (Object != null && Object.IsValid && Object.HasInputAuthority) return true;
                }
                catch
                {
                    ConsoleMessage.Send(true, "Networked Transform - Error checking Input Authority", Color.yellow);
                }

                return Runner.IsSharedModeMasterClient && !IsSpawnerConnected;
            }
        }

        /// <summary>
        /// Gets called when the object is spawned in the network.
        /// </summary>
        public override void Spawned()
        {
            _cachedScale = transform.localScale;
            transform.localScale = Vector3.zero;

            _isGrabbableOnSpawn = IsGrabbable;
            _lastKnownSpawnerPlayerId = SpawnerPlayerId;
            _lastKnownSpawnerControlsTransform = SpawnerControlsTransform;

            InitializeSpawnerPlayerId();

            bool isLocalSpawner = Object.HasInputAuthority || (Runner.GameMode == GameMode.Shared && Object.HasStateAuthority);
            if (isLocalSpawner)
            {
                activeRadial = Instantiate(MUES_NetworkedObjectManager.Instance.loadingRadial, transform.position, Quaternion.identity);
                StartCoroutine(UpdateRadialPosition());
            }

            ConsoleMessage.Send(true, $"Networked Transform - Spawned: IsGrabbable={IsGrabbable}, SpawnerControlsTransform={SpawnerControlsTransform}, SpawnerPlayerId={SpawnerPlayerId}, InputAuth={Object.InputAuthority}, LocalPlayer={Runner?.LocalPlayer}, LocalPlayerId={Runner?.LocalPlayer.PlayerId}", Color.magenta);

            DisableExistingGrabbableComponents();
            StartCoroutine(InitRoutine());
        }

        /// <summary>
        /// Updates the loading radial position to follow the object until initialization is complete.
        /// </summary>
        private IEnumerator UpdateRadialPosition()
        {
            while (activeRadial != null && !initialized)
            {
                activeRadial.transform.position = transform.position;
                yield return null;
            }
        }

        /// <summary>
        /// Initializes the SpawnerPlayerId if this client has state authority and it hasn't been set.
        /// </summary>
        private void InitializeSpawnerPlayerId()
        {
            if (!Object.HasStateAuthority || SpawnerPlayerId != -1) return;

            SpawnerPlayerId = (Object.InputAuthority == PlayerRef.None || Object.InputAuthority.PlayerId < 0)
                ? Runner.LocalPlayer.PlayerId
                : Object.InputAuthority.PlayerId;

            ConsoleMessage.Send(true, $"Networked Transform - SpawnerPlayerId set to: {SpawnerPlayerId}", Color.cyan);
        }

        /// <summary>
        /// Disables any existing grabbable components on the object until permissions are verified.
        /// </summary>
        private void DisableExistingGrabbableComponents()
        {
            if (TryGetComponent<Grabbable>(out var grabbable)) grabbable.enabled = false;
            if (TryGetComponent<GrabInteractable>(out var grabInteractable)) grabInteractable.enabled = false;
            if (TryGetComponent<HandGrabInteractable>(out var handGrab)) handGrab.enabled = false;
            if (TryGetComponent<TransferOwnershipFusion>(out var ownership)) ownership.enabled = false;
        }

        /// <summary>
        /// Gets the anchor and initializes the object's position and rotation based on networked data.
        /// </summary>
        private IEnumerator InitRoutine()
        {
            yield return null;

            ApplySpawnerOnlyGrabSetting();

            transform.GetPositionAndRotation(out Vector3 spawnWorldPos, out Quaternion spawnWorldRot);
            bool hadValidSpawnPos = spawnWorldPos != Vector3.zero;

            ConsoleMessage.Send(true, $"Networked Transform - Cached spawn: Pos={spawnWorldPos}, Rot={spawnWorldRot.eulerAngles}, valid={hadValidSpawnPos}", Color.cyan);

            yield return InitAnchorRoutine();

            yield return WaitForCondition(
                () => MUES_SessionMeta.Instance != null,
                DefaultTimeout,
                () => ConsoleMessage.Send(true, "Networked Transform - Waiting for session meta...", Color.yellow)
            );

            _ownershipTransfer = GetComponent<TransferOwnershipFusion>();

            bool isSpawner = Object.HasInputAuthority || (Runner.GameMode == GameMode.Shared && Object.HasStateAuthority);

            if (isSpawner) yield return InitAsSpawner(spawnWorldPos, spawnWorldRot, hadValidSpawnPos);
            else yield return InitAsNonSpawner();

            yield return LoadModelIfNeeded();
            UpdateGrabbableState();

            CacheCurrentPosition();
            initialized = true;
            transform.localScale = _cachedScale;

            ConsoleMessage.Send(true, $"Networked Transform - Init complete. Final Pos={transform.position}, Rot={transform.rotation.eulerAngles}, Scale restored to {_cachedScale}", Color.green);

            Destroy(activeRadial);
            OnInitialized?.Invoke();
        }

        /// <summary>
        /// Applies the spawnerOnlyGrab inspector setting to the network if applicable.
        /// </summary>
        private void ApplySpawnerOnlyGrabSetting()
        {
            if (!HasAuthority || SpawnerControlsTransform || !spawnerOnlyGrab) return;

            SpawnerControlsTransform = true;
            ConsoleMessage.Send(true, $"Networked Transform - Applied inspector spawnerOnlyGrab={spawnerOnlyGrab} to network", Color.cyan);
        }

        /// <summary>
        /// Initializes the object as the spawner, restoring position and setting anchor offsets.
        /// </summary>
        private IEnumerator InitAsSpawner(Vector3 spawnWorldPos, Quaternion spawnWorldRot, bool hadValidSpawnPos)
        {
            yield return null;

            if (hadValidSpawnPos)
            {
                transform.SetPositionAndRotation(spawnWorldPos, spawnWorldRot);
                ConsoleMessage.Send(true, $"Networked Transform - Restored spawn position after parenting: {spawnWorldPos}", Color.cyan);
            }

            if (transform.position == Vector3.zero)
                ConsoleMessage.Send(true, "Networked Transform - WARNING: Position is still at origin after restore!", Color.red);

            WorldToAnchor();
            ConsoleMessage.Send(true, $"Networked Transform - WorldToAnchor set: Pos={transform.position}, Offset={LocalAnchorOffset}, RotOffset={LocalAnchorRotationOffset.eulerAngles}", Color.cyan);
        }

        /// <summary>
        /// Initializes the object as a non-spawner, waiting for anchor offsets from the network.
        /// </summary>
        private IEnumerator InitAsNonSpawner()
        {
            bool receivedOffset = false;

            yield return WaitForCondition(
                () =>
                {
                    receivedOffset = LocalAnchorOffset != Vector3.zero || LocalAnchorRotationOffset != Quaternion.identity;
                    return receivedOffset;
                },
                DefaultTimeout
            );

            if (!receivedOffset)
                ConsoleMessage.Send(true, "Networked Transform - Timeout waiting for anchor offset!", Color.yellow);
            else
                ConsoleMessage.Send(true, $"Networked Transform - Received anchor offset: {LocalAnchorOffset}, rot: {LocalAnchorRotationOffset.eulerAngles}", Color.cyan);

            if (anchorReady && anchor != null)
            {
                AnchorToWorld();
                ConsoleMessage.Send(true, $"Networked Transform - AnchorToWorld applied: NewPos={transform.position}, NewRot={transform.rotation.eulerAngles}", Color.cyan);
            }
            else
                ConsoleMessage.Send(true, "Networked Transform - Anchor not ready for AnchorToWorld!", Color.red);
        }

        /// <summary>
        /// Caches the current position relative to the reference transform or in world space.
        /// </summary>
        private void CacheCurrentPosition()
        {
            var refTransform = ReferenceTransform;

            if (refTransform != null)
            {
                _lastLocalPosition = refTransform.InverseTransformPoint(transform.position);
                _lastLocalRotation = Quaternion.Inverse(refTransform.rotation) * transform.rotation;
            }
            else
            {
                _lastLocalPosition = transform.position;
                _lastLocalRotation = transform.rotation;
            }
        }

        /// <summary>
        /// Checks if the object has moved significantly.
        /// </summary>
        private bool HasMovedSignificantly()
        {
            var refTransform = ReferenceTransform;

            Vector3 currentPos;
            Quaternion currentRot;

            if (refTransform != null)
            {
                currentPos = refTransform.InverseTransformPoint(transform.position);
                currentRot = Quaternion.Inverse(refTransform.rotation) * transform.rotation;
            }
            else
            {
                currentPos = transform.position;
                currentRot = transform.rotation;
            }

            bool hasMoved = Vector3.Distance(_lastLocalPosition, currentPos) > _movementThreshold ||
                            Quaternion.Angle(_lastLocalRotation, currentRot) > _rotationThreshold;

            if (hasMoved)
            {
                _lastLocalPosition = currentPos;
                _lastLocalRotation = currentRot;
            }

            return hasMoved;
        }

        /// <summary>
        /// Gets called on each network tick to update the object's position if necessary.
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (!HasAuthority || !initialized || !anchorReady) return;

            if (_isBeingGrabbed || HasMovedSignificantly())
                WorldToAnchor();
        }

        /// <summary>
        /// Gets called every frame to update the object's position based on authority and networked properties.
        /// </summary>
        public override void Render()
        {
            if (!initialized || !anchorReady) return;

            if (!HasAuthority)
                AnchorToWorld();

            CheckForNetworkPropertyChanges();
        }

        /// <summary>
        /// Checks for changes in networked properties and updates grabbable state accordingly.
        /// </summary>
        private void CheckForNetworkPropertyChanges()
        {
            if (_lastKnownSpawnerPlayerId != SpawnerPlayerId)
            {
                ConsoleMessage.Send(true, $"Networked Transform - SpawnerPlayerId changed from {_lastKnownSpawnerPlayerId} to {SpawnerPlayerId} on {gameObject.name}", Color.cyan);
                _lastKnownSpawnerPlayerId = SpawnerPlayerId;
                UpdateGrabbableState();
            }

            if (_lastKnownSpawnerControlsTransform != SpawnerControlsTransform)
            {
                ConsoleMessage.Send(true, $"Networked Transform - SpawnerControlsTransform changed from {_lastKnownSpawnerControlsTransform} to {SpawnerControlsTransform} on {gameObject.name}", Color.cyan);
                _lastKnownSpawnerControlsTransform = SpawnerControlsTransform;
                UpdateGrabbableState();
            }

            if (Time.frameCount % 60 == 0 && _grabbable != null && _grabbable.enabled != IsLocalPlayerAllowedToControl)
                UpdateGrabbableState();
        }

        #region Model Loading

        /// <summary>
        /// Loads the model from the ModelFileName property if it's set.
        /// </summary>
        private IEnumerator LoadModelIfNeeded()
        {
            yield return WaitForCondition(
                () => !IsObjectValid || !string.IsNullOrEmpty(ModelFileName.ToString()),
                DefaultTimeout
            );

            if (!IsObjectValid) yield break;

            string modelName = ModelFileName.ToString();

            if (string.IsNullOrEmpty(modelName))
            {
                ConsoleMessage.Send(true, "Networked Transform - No model filename set - using static prefab.", Color.cyan);
                yield break;
            }

            if (_modelLoaded || transform.childCount > 0)
            {
                ConsoleMessage.Send(true, "Networked Transform - Model already loaded.", Color.yellow);
                yield break;
            }

            ConsoleMessage.Send(true, $"Networked Transform - Loading model: {modelName}", Color.cyan);

            var objectManager = MUES_NetworkedObjectManager.Instance;
            if (objectManager == null)
            {
                ConsoleMessage.Send(true, "Networked Transform - NetworkedObjectManager not available.", Color.red);
                yield break;
            }

            var fetchTask = objectManager.FetchModelFromServer(modelName);
            yield return new WaitUntil(() => fetchTask.IsCompleted);

            string localPath = fetchTask.Result;
            if (string.IsNullOrEmpty(localPath))
            {
                ConsoleMessage.Send(true, $"Networked Transform - Failed to fetch model: {modelName}", Color.red);
                yield break;
            }

            var loadTask = LoadModelLocally(localPath);
            yield return new WaitUntil(() => loadTask.IsCompleted);

            _modelLoaded = true;

            if (_isGrabbableOnSpawn)
                yield return FinalizeGrabbableModel();

            ConsoleMessage.Send(true, "Networked Transform - Object is now ready for interaction", Color.green);
        }

        /// <summary>
        /// Finalizes the grabbable model by initializing components and updating state.
        /// </summary>
        private IEnumerator FinalizeGrabbableModel()
        {
            InitGrabbableComponents();
            yield return null;

            if (!IsObjectValid) yield break;

            UpdateGrabbableState();

            bool isAllowed = false;
            try { isAllowed = IsLocalPlayerAllowedToControl; } catch { }

            ConsoleMessage.Send(true, $"Networked Transform - Model loaded and grabbable initialized. IsAllowed={isAllowed}", Color.green);

            if (anchorReady)
            {
                if (HasAuthority) WorldToAnchor();
                else AnchorToWorld();
            }
        }

        /// <summary>
        /// Loads a GLB model and attaches it as a child of this transform.
        /// </summary>
        private async Task LoadModelLocally(string path)
        {
            var objectManager = MUES_NetworkedObjectManager.Instance;
            if (objectManager != null)
                await objectManager.WaitForInstantiationPermit();

            try
            {
                await LoadAndInstantiateGltf(path);
            }
            finally
            {
                objectManager?.ReleaseInstantiationPermit();
            }
        }

        /// <summary>
        /// Loads and instantiates a GLTF model from the specified path.
        /// </summary>
        private async Task LoadAndInstantiateGltf(string path)
        {
            var gltfImport = new GltfImport(materialGenerator: CreateMaterialGenerator());

            var settings = new ImportSettings
            {
                GenerateMipMaps = true,
                AnisotropicFilterLevel = 3,
                NodeNameMethod = NameImportMethod.OriginalUnique
            };

            if (!await TryLoadGltf(gltfImport, path, settings)) return;

            ConsoleMessage.Send(true, "Networked Transform - Starting GLTF Instantiation...", Color.cyan);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var wrapper = CreateGltfWrapper();
            bool instantiateSuccess = await gltfImport.InstantiateMainSceneAsync(wrapper.transform);

            stopwatch.Stop();
            ConsoleMessage.Send(true, $"Networked Transform - GLTF Instantiation took {stopwatch.ElapsedMilliseconds}ms.", Color.cyan);

            if (!instantiateSuccess)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Failed to instantiate GLB: {path}", Color.red);
                return;
            }

            PostProcessModel(wrapper.transform);

            ConsoleMessage.Send(true, "Networked Transform - Starting Collider Generation...", Color.cyan);
            await AddMeshCollidersAsync(wrapper.transform);

            ConsoleMessage.Send(true, $"Networked Transform - Model loaded successfully: {path}", Color.green);
        }

        /// <summary>
        /// Attempts to load a GLTF file, handling errors and cleaning up corrupt files.
        /// </summary>
        private async Task<bool> TryLoadGltf(GltfImport gltfImport, string path, ImportSettings settings)
        {
            bool success = false;
            try
            {
                success = await gltfImport.Load($"file://{path}", settings);
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Exception checking GLB: {ex.Message}", Color.red);
            }

            if (!success)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Failed to load GLB: {path}. Deleting potential corrupt file.", Color.red);
                TryDeleteFile(path);
            }

            return success;
        }

        /// <summary>
        /// Attempts to delete a file, suppressing any errors.
        /// </summary>
        private void TryDeleteFile(string path)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// Creates a wrapper GameObject for the GLTF model.
        /// </summary>
        private GameObject CreateGltfWrapper()
        {
            var wrapper = new GameObject("GLTF_Wrapper");
            wrapper.transform.SetParent(transform, false);
            wrapper.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            return wrapper;
        }

        /// <summary>
        /// Applies post-processing to the loaded model including layer setup and occlusion settings.
        /// </summary>
        private void PostProcessModel(Transform wrapper)
        {
            SetLayerRecursively(wrapper, LayerMask.NameToLayer("Default"));
            DisableEnvironmentDepthOcclusionForModel(wrapper);
        }

        /// <summary>
        /// Sets the layer recursively for all child objects.
        /// </summary>
        private void SetLayerRecursively(Transform parent, int layer)
        {
            parent.gameObject.layer = layer;
            foreach (Transform child in parent)
                SetLayerRecursively(child, layer);
        }

        /// <summary>
        /// Disables environment depth occlusion for all renderers in the model.
        /// </summary>
        private void DisableEnvironmentDepthOcclusionForModel(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                foreach (var material in renderer.materials)
                    ConfigureMaterialOcclusion(material);
            }

            ConsoleMessage.Send(true, $"Networked Transform - Disabled environment depth occlusion for {renderers.Length} renderers.", Color.cyan);
        }

        /// <summary>
        /// Configures occlusion settings for a single material.
        /// </summary>
        private void ConfigureMaterialOcclusion(Material material)
        {
            if (material == null) return;

            if (material.HasProperty("_EnvironmentDepthBias"))
                material.SetFloat("_EnvironmentDepthBias", 1.0f);

            if (material.HasProperty("_EnableOcclusionHardDepthSensitivity"))
                material.SetFloat("_EnableOcclusionHardDepthSensitivity", 0f);

            material.DisableKeyword("HARD_OCCLUSION");
            material.DisableKeyword("SOFT_OCCLUSION");
            material.EnableKeyword("_ENVIRONMENTDEPTHOCCLUSION_OFF");
        }

        /// <summary>
        /// Creates the appropriate material generator based on the current render pipeline.
        /// </summary>
        private IMaterialGenerator CreateMaterialGenerator()
        {
#if USING_URP
            var renderPipelineAsset = GraphicsSettings.currentRenderPipeline;

            if (renderPipelineAsset is UniversalRenderPipelineAsset urpAsset)
                return new UniversalRPMaterialGenerator(urpAsset);
#endif
            return null;
        }

        /// <summary>
        /// Recursively adds MeshColliders to all children with MeshFilters.
        /// </summary>
        private async Task AddMeshCollidersAsync(Transform parent)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var nodesToProcess = new List<Transform>();
            GetChildrenRecursive(parent, nodesToProcess);

            ConsoleMessage.Send(true, $"Networked Transform - Processing {nodesToProcess.Count} nodes for colliders.", Color.cyan);

            foreach (Transform child in nodesToProcess)
            {
                TryAddMeshCollider(child);

                if (stopwatch.ElapsedMilliseconds > 8)
                {
                    await Task.Yield();
                    stopwatch.Restart();
                }
            }

            ConsoleMessage.Send(true, "Networked Transform - Collider Generation finished.", Color.green);
        }

        /// <summary>
        /// Attempts to add a mesh collider to a transform if it has a valid mesh filter.
        /// </summary>
        private void TryAddMeshCollider(Transform child)
        {
            if (!child.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) return;
            if (child.TryGetComponent<MeshCollider>(out _)) return;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            MeshCollider col = child.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = filter.sharedMesh;
            col.convex = filter.sharedMesh.vertexCount <= 5000;

            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds > 20)
                ConsoleMessage.Send(true, $"Networked Transform - Single collider gen took {stopwatch.ElapsedMilliseconds}ms for {child.name} (Verts: {filter.sharedMesh.vertexCount})", Color.yellow);
        }

        /// <summary>
        /// Gets all child transforms recursively.
        /// </summary>
        private void GetChildrenRecursive(Transform parent, List<Transform> list)
        {
            foreach (Transform child in parent)
            {
                list.Add(child);
                if (child.childCount > 0)
                    GetChildrenRecursive(child, list);
            }
        }

        #endregion

        #region Grabbable Components

        /// <summary>
        /// Initializes the necessary components to make the object grabbable in a networked environment.
        /// </summary>
        public void InitGrabbableComponents()
        {
            if (_grabbable != null)
            {
                ConsoleMessage.Send(true, "Networked Transform - Object already grabbable", Color.yellow);
                return;
            }

            var rb = EnsureRigidbody();

            _grabbable = gameObject.AddComponent<Grabbable>();
            _grabInteractable = gameObject.AddComponent<GrabInteractable>();
            _handGrabInteractable = gameObject.AddComponent<HandGrabInteractable>();

            _grabbable.InjectOptionalRigidbody(rb);
            _grabInteractable.InjectOptionalPointableElement(_grabbable);
            _grabInteractable.InjectRigidbody(rb);
            _handGrabInteractable.InjectOptionalPointableElement(_grabbable);
            _handGrabInteractable.InjectRigidbody(rb);

            gameObject.AddComponent<TransferOwnershipOnSelect>();
            _ownershipTransfer = GetComponent<TransferOwnershipFusion>() ?? gameObject.AddComponent<TransferOwnershipFusion>();
            _grabbable.WhenPointerEventRaised += OnPointerEvent;

            SetGrabbableComponentsEnabled(false);

            ConsoleMessage.Send(true, $"Networked Transform - Grabbable components added (initially disabled). Grabbable={_grabbable != null}, GrabInteractable={_grabInteractable != null}, HandGrab={_handGrabInteractable != null}", Color.green);
        }

        /// <summary>
        /// Ensures a Rigidbody component exists on the object, creating one if necessary.
        /// </summary>
        private Rigidbody EnsureRigidbody()
        {
            if (TryGetComponent<Rigidbody>(out var rb)) return rb;

            rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            return rb;
        }

        /// <summary>
        /// Helper method to enable/disable all grabbable-related components at once.
        /// </summary>
        private void SetGrabbableComponentsEnabled(bool enabled)
        {
            if (_grabbable != null) _grabbable.enabled = enabled;
            if (_grabInteractable != null) _grabInteractable.enabled = enabled;
            if (_handGrabInteractable != null) _handGrabInteractable.enabled = enabled;
            if (_ownershipTransfer != null) _ownershipTransfer.enabled = enabled;
        }

        /// <summary>
        /// Handles pointer events for grab detection.
        /// </summary>
        private void OnPointerEvent(PointerEvent evt)
        {
            switch (evt.Type)
            {
                case PointerEventType.Select:
                    OnGrabbed();
                    break;
                case PointerEventType.Unselect:
                case PointerEventType.Cancel:
                    OnReleased();
                    break;
            }
        }

        /// <summary>
        /// Called when the object is grabbed.
        /// </summary>
        public void OnGrabbed() => _isBeingGrabbed = true;

        /// <summary>
        /// Called when the object is released.
        /// </summary>
        public void OnReleased()
        {
            _isBeingGrabbed = false;
            CacheCurrentPosition();
        }

        /// <summary>
        /// Deletes this networked object. Only allowed for players who have control permission.
        /// </summary>
        public void Delete()
        {
            if (Runner == null || !IsObjectValid)
            {
                ConsoleMessage.Send(true, "Networked Transform - Cannot delete: Runner or Object invalid.", Color.red);
                return;
            }

            if (!IsLocalPlayerAllowedToControl)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Cannot delete: Local player not allowed to control this object. SpawnerPlayerId={SpawnerPlayerId}, LocalPlayer={Runner.LocalPlayer.PlayerId}", Color.yellow);
                return;
            }

            ConsoleMessage.Send(true, $"Networked Transform - Deleting object: {gameObject.name}", Color.cyan);

            if (Object.HasStateAuthority)
            {
                Runner.Despawn(Object);
                ConsoleMessage.Send(true, $"Networked Transform - Object despawned: {gameObject.name}", Color.green);
            }
            else
            {
                Object.RequestStateAuthority();
                StartCoroutine(DeleteAfterAuthority());
            }
        }

        /// <summary>
        /// Waits for state authority before despawning the object.
        /// </summary>
        private IEnumerator DeleteAfterAuthority()
        {
            bool hasAuthority = false;

            yield return WaitForCondition(
                () =>
                {
                    if (!IsObjectValid) return true;
                    hasAuthority = Object.HasStateAuthority;
                    return hasAuthority;
                },
                DefaultTimeout
            );

            if (!IsObjectValid) yield break;

            if (hasAuthority)
            {
                Runner.Despawn(Object);
                ConsoleMessage.Send(true, $"Networked Transform - Object despawned after authority transfer: {gameObject.name}", Color.green);
            }
            else
            {
                ConsoleMessage.Send(true, $"Networked Transform - Timeout waiting for authority to delete: {gameObject.name}", Color.yellow);
            }
        }

        /// <summary>
        /// Updates the grabbable state based on network properties.
        /// </summary>
        private void UpdateGrabbableState()
        {
            EnsureGrabbableReferences();

            if (_grabbable == null && _grabInteractable == null && _handGrabInteractable == null)
                return;

            bool isAllowed;
            try { isAllowed = IsLocalPlayerAllowedToControl; }
            catch { isAllowed = false; }

            SetGrabbableComponentsEnabled(isAllowed);
            LogGrabbableState(isAllowed);
        }

        /// <summary>
        /// Ensures all grabbable component references are up to date.
        /// </summary>
        private void EnsureGrabbableReferences()
        {
            if (_grabbable != null) return;

            _grabbable = GetComponent<Grabbable>();
            _grabInteractable = GetComponent<GrabInteractable>();
            _handGrabInteractable = GetComponent<HandGrabInteractable>();
            _ownershipTransfer = GetComponent<TransferOwnershipFusion>();
        }

        /// <summary>
        /// Logs the current grabbable state for debugging purposes.
        /// </summary>
        private void LogGrabbableState(bool isAllowed)
        {
            if (!isAllowed)
            {
                try
                {
                    ConsoleMessage.Send(true, $"Networked Transform - Grabbable DISABLED for local player (SpawnerControlsTransform={SpawnerControlsTransform}, SpawnerPlayerId={SpawnerPlayerId}, LocalPlayer={Runner?.LocalPlayer.PlayerId}) on {gameObject.name}", Color.yellow);
                }
                catch
                {
                    ConsoleMessage.Send(true, $"Networked Transform - Grabbable DISABLED on {gameObject.name}", Color.yellow);
                }
            }
            else
            {
                ConsoleMessage.Send(true, $"Networked Transform - Grabbable ENABLED for local player on {gameObject.name}", Color.green);
            }
        }

        /// <summary>
        /// Public method to force update grabbable state (called after migration).
        /// </summary>
        public void RefreshGrabbableState() => UpdateGrabbableState();

        /// <summary>
        /// Called by the new master client to take over ownership if the original spawner left.
        /// </summary>
        public void TransferSpawnerOwnership()
        {
            if (Runner == null || !SpawnerControlsTransform || IsSpawnerConnected) return;

            StartCoroutine(TransferSpawnerOwnershipAsync());
        }

        /// <summary>
        /// Async coroutine to properly wait for StateAuthority before transferring spawner ownership.
        /// </summary>
        private IEnumerator TransferSpawnerOwnershipAsync()
        {
            if (Runner == null || !IsObjectValid) yield break;

            ConsoleMessage.Send(true, $"Networked Transform - Starting async spawner ownership transfer for {gameObject.name}", Color.cyan);

            if (!TryRequestStateAuthority()) yield break;

            bool hasAuth = Object.HasStateAuthority;

            if (!hasAuth)
            {
                yield return WaitForCondition(
                    () => !IsObjectValid || Object.HasStateAuthority,
                    DefaultTimeout
                );

                hasAuth = IsObjectValid && Object.HasStateAuthority;
            }

            if (!hasAuth)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Timeout waiting for StateAuthority on {gameObject.name}", Color.yellow);
                yield break;
            }

            if (!IsSpawnerConnected)
                ExecuteOwnershipTransfer();
        }

        /// <summary>
        /// Attempts to request state authority, returning true if successful or already possessed.
        /// </summary>
        private bool TryRequestStateAuthority()
        {
            try
            {
                if (Object.HasStateAuthority) return true;

                Object.RequestStateAuthority();
                ConsoleMessage.Send(true, $"Networked Transform - Requested StateAuthority for {gameObject.name}", Color.cyan);
                return true;
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Error requesting StateAuthority: {ex.Message}", Color.yellow);
                return false;
            }
        }

        /// <summary>
        /// Executes the actual ownership transfer to the local player.
        /// </summary>
        private void ExecuteOwnershipTransfer()
        {
            try
            {
                int oldSpawnerId = SpawnerPlayerId;
                SpawnerPlayerId = Runner.LocalPlayer.PlayerId;
                Object.AssignInputAuthority(Runner.LocalPlayer);

                ConsoleMessage.Send(true, $"Networked Transform - Transferred spawner ownership from {oldSpawnerId} to {SpawnerPlayerId}", Color.green);
                UpdateGrabbableState();
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Error transferring ownership: {ex.Message}", Color.yellow);
            }
        }

        private void OnDestroy()
        {
            if (_grabbable != null)
                _grabbable.WhenPointerEventRaised -= OnPointerEvent;
        }

        #endregion
    }
}

#if UNITY_EDITOR

[CustomEditor(typeof(MUES_NetworkedTransform))]
public class MUES_NetworkedTransformEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MUES_NetworkedTransform obj = (MUES_NetworkedTransform)target;

        if (obj.GetComponent<Grabbable>() != null) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editor Only:", EditorStyles.boldLabel);

        if (GUILayout.Button("Make Grabbable"))
        {
            obj.InitGrabbableComponents();
            EditorUtility.SetDirty(obj);
        }

        EditorGUILayout.Space();
    }
}

#endif