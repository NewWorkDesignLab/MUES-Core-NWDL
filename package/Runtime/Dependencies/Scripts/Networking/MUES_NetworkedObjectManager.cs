using Fusion;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MUES.Core
{
    public class MUES_NetworkedObjectManager : MonoBehaviour
    {
        [Header("Model Loading Settings")]
        [Tooltip("The networked container prefab used to load GLB models.")]
        public NetworkObject loadedModelContainer;
        [Tooltip("A radial loading indicator prefab to show while models are downloading.")]
        public GameObject loadingRadial;
        [Tooltip("The API key used for authenticating model download requests.")]
        public string modelApiKey;
        [Tooltip("Enable to see debug messages in the console.")]
        public bool debugMode;

        private readonly Dictionary<string, Task<string>> _activeDownloads = new Dictionary<string, Task<string>>();    // Tracks active model download tasks
        private readonly SemaphoreSlim _instantiationSemaphore = new SemaphoreSlim(1, 1); // Semaphore to limit concurrent instantiations

        private const float DefaultTimeout = 7f;    // Default timeout for waiting operations

        /// <summary>
        /// Gets the MUES_Networking singleton instance.
        /// </summary>
        private MUES_Networking Net => MUES_Networking.Instance;

        /// <summary>
        /// Gets the room visualizer singleton instance.
        /// </summary>
        private MUES_RoomVisualizer RoomVis => MUES_RoomVisualizer.Instance;

        public static MUES_NetworkedObjectManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
        }

        /// <summary>
        /// Instantiates a networked model at a position in front of the main camera. For remote clients, the position is converted relative to the virtualRoom anchor.
        /// </summary>
        public void Instantiate(MUES_NetworkedTransform modelToInstantiate, Vector3 position, Quaternion rotation, out MUES_NetworkedTransform instantiatedModel)
        {
            instantiatedModel = null;

            bool isChairPlacement = RoomVis != null && RoomVis.chairPlacementActive;

            if (!isChairPlacement && !Net.isConnected)
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Not connected - cannot instantiate networked models.", Color.yellow);
                return;
            }

            if (!ValidateSpawnContext(position, out string contextInfo))
            {
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] {contextInfo}", Color.red);
                return;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Instantiate: SpawnPos={position}, isRemote={Net.isRemote}", Color.cyan);

            var spawnedNetworkObject = Net.Runner.Spawn(modelToInstantiate, position, rotation, Net.Runner.LocalPlayer);

            if (spawnedNetworkObject != null)
                instantiatedModel = spawnedNetworkObject.GetComponent<MUES_NetworkedTransform>();

            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Networked model instantiated.", Color.green);
        }

        /// <summary>
        /// Validates the spawn context and logs appropriate debug information.
        /// </summary>
        private bool ValidateSpawnContext(Vector3 position, out string errorMessage)
        {
            errorMessage = null;

            if (Net.isRemote)
            {
                var virtualRoom = RoomVis?.virtualRoom;
                if (virtualRoom != null)
                {
                    ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Remote client spawn: worldPos={position}, virtualRoom.pos={virtualRoom.transform.position}, sceneParent.pos={Net.sceneParent?.position}", Color.cyan);
                    return true;
                }

                errorMessage = "Remote client has no virtualRoom yet - spawn position may be incorrect!";
                return false;
            }

            if (Net.sceneParent == null)
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Colocated client has no sceneParent - spawn position may be incorrect!", Color.yellow);
            else
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Colocated client spawning at pos: {position}, sceneParent.pos={Net.sceneParent.position}", Color.cyan);

            return true;
        }

        /// <summary>
        /// Spawns a networked container that will load the GLB model on all clients. For non-master clients, this sends a request to the host to spawn the model.
        /// </summary>
        public void InstantiateFromServer(string modelFileName, Vector3 position, Quaternion rotation, bool makeGrabbable, bool spawnerGrabOnly = false)
        {
            if (!Net.isConnected)
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Not connected - cannot instantiate networked models.", Color.yellow);
                return;
            }

            var runner = Net.Runner;
            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Calculated spawn pos: {position}, rot: {rotation.eulerAngles}", Color.cyan);

            if (runner.IsSharedModeMasterClient)
            {
                SpawnModelContainer(modelFileName, makeGrabbable, spawnerGrabOnly, runner.LocalPlayer, position, rotation);
                return;
            }

            if (!TryGetAnchorRelativeTransform(position, rotation, out Vector3 relativePos, out Quaternion relativeRot))
                return;

            RequestSpawnFromSessionMeta(modelFileName, makeGrabbable, spawnerGrabOnly, runner.LocalPlayer, relativePos, relativeRot);
        }

        /// <summary>
        /// Attempts to convert world position and rotation to anchor-relative coordinates.
        /// </summary>
        private bool TryGetAnchorRelativeTransform(Vector3 worldPos, Quaternion worldRot, out Vector3 relativePos, out Quaternion relativeRot)
        {
            relativePos = worldPos;
            relativeRot = worldRot;

            Transform anchorReference = GetAnchorReference();

            if (anchorReference == null && Net.isRemote)
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Remote client has no virtualRoom - cannot calculate anchor-relative position!", Color.red);
                return false;
            }

            if (anchorReference != null)
            {
                relativePos = anchorReference.InverseTransformPoint(worldPos);
                relativeRot = Quaternion.Inverse(anchorReference.rotation) * worldRot;
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Client converting spawn: pos {worldPos} -> {relativePos}, rot {worldRot.eulerAngles} -> {relativeRot.eulerAngles}", Color.cyan);
            }

            return true;
        }

        /// <summary>
        /// Gets the appropriate anchor reference transform based on client type.
        /// </summary>
        private Transform GetAnchorReference()
        {
            if (Net.isRemote)
            {
                var virtualRoom = RoomVis?.virtualRoom;
                if (virtualRoom != null)
                {
                    ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Remote client using virtualRoom as anchor reference at {virtualRoom.transform.position}", Color.cyan);
                    return virtualRoom.transform;
                }
                return null;
            }

            if (Net.sceneParent != null)
            {
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Colocated client using sceneParent as anchor reference at {Net.sceneParent.position}", Color.cyan);
                return Net.sceneParent;
            }

            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Colocated client has no sceneParent - sending world position as fallback.", Color.yellow);
            return null;
        }

        /// <summary>
        /// Requests the session meta to spawn a model on the master client.
        /// </summary>
        private void RequestSpawnFromSessionMeta(string modelFileName, bool makeGrabbable, bool spawnerGrabOnly, PlayerRef ownerPlayer, Vector3 spawnPos, Quaternion spawnRot)
        {
            if (MUES_SessionMeta.Instance != null)
                MUES_SessionMeta.Instance.RequestSpawnModel(modelFileName, makeGrabbable, spawnerGrabOnly, ownerPlayer, spawnPos, spawnRot);
            else
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] SessionMeta not available - cannot request spawn.", Color.red);
        }

        /// <summary>
        /// Actually spawns the model container. Only called on the master client.
        /// </summary>
        public void SpawnModelContainer(string modelFileName, bool makeGrabbable, bool spawnerGrabOnly, PlayerRef ownerPlayer, Vector3 worldSpawnPos, Quaternion worldSpawnRot)
        {
            var runner = Net.Runner;

            if (!runner.IsSharedModeMasterClient)
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Cannot spawn - not master client.", Color.yellow);
                return;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Spawning at world position: {worldSpawnPos}, rotation: {worldSpawnRot.eulerAngles}", Color.cyan);

            var container = runner.Spawn(loadedModelContainer, worldSpawnPos, worldSpawnRot, ownerPlayer,
                onBeforeSpawned: (r, obj) => ConfigureNetworkTransformBeforeSpawn(obj, modelFileName, makeGrabbable, spawnerGrabOnly, ownerPlayer)
            );

            container.name = $"ModelContainer_{modelFileName}";

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Spawned networked container for: {modelFileName} at {worldSpawnPos} (Owner={ownerPlayer}, SpawnerOnly={spawnerGrabOnly})", Color.green);
        }

        /// <summary>
        /// Configures the MUES_NetworkedTransform component before the object is spawned.
        /// </summary>
        private void ConfigureNetworkTransformBeforeSpawn(NetworkObject obj, string modelFileName, bool makeGrabbable, bool spawnerGrabOnly, PlayerRef ownerPlayer)
        {
            var netTransform = obj.GetComponent<MUES_NetworkedTransform>();
            if (netTransform == null) return;

            netTransform.ModelFileName = modelFileName;
            netTransform.SpawnerControlsTransform = spawnerGrabOnly;
            netTransform.IsGrabbable = makeGrabbable;
            netTransform.SpawnerPlayerId = ownerPlayer.PlayerId;

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] OnBeforeSpawned: Set IsGrabbable={makeGrabbable}, SpawnerControlsTransform={spawnerGrabOnly}, SpawnerPlayerId={ownerPlayer.PlayerId}", Color.cyan);
        }

        /// <summary>
        /// Downloads a GLB model from the server and caches it locally.
        /// </summary>
        public Task<string> FetchModelFromServer(string modelFileName)
        {
            if (_activeDownloads.TryGetValue(modelFileName, out var existingTask))
                return existingTask;

            var task = FetchModelFromServerInternal(modelFileName);
            _activeDownloads[modelFileName] = task;

            _ = task.ContinueWith(_ => _activeDownloads.Remove(modelFileName));

            return task;
        }

        /// <summary>
        /// Fetches the model from the server and saves it locally.
        /// </summary>
        private async Task<string> FetchModelFromServerInternal(string modelFileName)
        {
            string filePath = GetModelFilePath(modelFileName);

            if (TryGetCachedModel(filePath, out string cachedPath))
                return cachedPath;

            return await DownloadModel(modelFileName, filePath);
        }

        /// <summary>
        /// Gets the full file path for a model in the cache directory.
        /// </summary>
        private string GetModelFilePath(string modelFileName)
        {
            string targetDirectory = Path.Combine(Application.persistentDataPath, "Models");
            if (!Directory.Exists(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            return Path.Combine(targetDirectory, modelFileName);
        }

        /// <summary>
        /// Attempts to retrieve a cached model from local storage.
        /// </summary>
        private bool TryGetCachedModel(string filePath, out string cachedPath)
        {
            cachedPath = null;

            if (!File.Exists(filePath))
                return false;

            FileInfo info = new FileInfo(filePath);
            if (info.Length > 0)
            {
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Model already cached at: {filePath}", Color.green);
                cachedPath = filePath;
                return true;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Cached model is empty/corrupt, deleting: {filePath}", Color.yellow);
            TryDeleteFile(filePath);
            return false;
        }

        /// <summary>
        /// Downloads a model from the server and saves it to the specified path.
        /// </summary>
        private async Task<string> DownloadModel(string modelFileName, string filePath)
        {
            string tempFilePath = filePath + ".tmp";
            string baseUrl = Net.modelDownloadDomain.TrimEnd('/');
            string url = $"{baseUrl}/{modelFileName}?api_key={modelApiKey}";

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Downloading model from: {url}", Color.cyan);

            using UnityWebRequest uwr = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
            uwr.downloadHandler = new DownloadHandlerFile(tempFilePath);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DefaultTimeout * 10));

            var operation = uwr.SendWebRequest();
            while (!operation.isDone)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    uwr.Abort();
                    ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Download timed out.", Color.red);
                    TryDeleteFile(tempFilePath);
                    return null;
                }
                await Task.Yield();
            }

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[MUES_ModelManager] Download failed: {uwr.error}");
                TryDeleteFile(tempFilePath);
                return null;
            }

            if (!TryFinalizeTempFile(tempFilePath, filePath))
                return null;

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Model downloaded and saved to: {filePath}", Color.green);
            return filePath;
        }

        /// <summary>
        /// Attempts to move the temporary file to the final destination.
        /// </summary>
        private bool TryFinalizeTempFile(string tempFilePath, string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                File.Move(tempFilePath, filePath);
                return true;
            }
            catch (IOException ex)
            {
                Debug.LogError($"[MUES_ModelManager] Failed to rename temp file to final model path: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Tries to delete a file, ignoring any exceptions.
        /// </summary>
        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Waits asynchronously until it's safe to instantiate the next model.
        /// </summary>
        public async Task WaitForInstantiationPermit()
        {
            if (_instantiationSemaphore != null)
                await _instantiationSemaphore.WaitAsync();
        }

        /// <summary>
        /// Releases the permit, allowing the next model in the queue to be instantiated.
        /// </summary>
        public void ReleaseInstantiationPermit()
        {
            try
            {
                if (_instantiationSemaphore != null && _instantiationSemaphore.CurrentCount == 0)
                    _instantiationSemaphore.Release();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MUES_NetworkedObjectManager] Error releasing semaphore: {ex.Message}");
            }
        }

        private void OnDestroy() => _instantiationSemaphore?.Dispose();
    }
}
