using Fusion;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class MUES_NetworkedObjectManager : MonoBehaviour
{
    [Header("Model Loading Settings")]
    [Tooltip("The networked container prefab used to load GLB models.")]
    public NetworkObject loadedModelContainer;
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode;

    private readonly Dictionary<string, Task<string>> _activeDownloads = new Dictionary<string, Task<string>>();    // Tracks active model download tasks

    private readonly System.Threading.SemaphoreSlim _instantiationSemaphore = new System.Threading.SemaphoreSlim(1, 1); // Semaphore to limit concurrent instantiations

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

        var net = MUES_Networking.Instance;
        bool isChairPlacement = MUES_RoomVisualizer.Instance != null && MUES_RoomVisualizer.Instance.chairPlacementActive;

        if (!isChairPlacement && !net.isConnected)
        {
            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Not connected - cannot instantiate networked models.", Color.yellow);
            return;
        }

        var runner = net.Runner;

        Vector3 spawnPos = position;
        Quaternion spawnRot = rotation;

        if (net.isRemote)
        {
            var virtualRoom = MUES_RoomVisualizer.Instance?.virtualRoom;

            if (virtualRoom != null)
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Remote client spawn: worldPos={position}, virtualRoom.pos={virtualRoom.transform.position}, sceneParent.pos={net.sceneParent?.position}", Color.cyan);
            else
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Remote client has no virtualRoom yet - spawn position may be incorrect!", Color.red);
                return;
            }
        }
        else
        {
            if (net.sceneParent == null)
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Colocated client has no sceneParent - spawn position may be incorrect!", Color.yellow);
            else
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Colocated client spawning at pos: {spawnPos}, sceneParent.pos={net.sceneParent.position}", Color.cyan);
        }

        ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Instantiate: SpawnPos={spawnPos}, isRemote={net.isRemote}", Color.cyan);

        var spawnedNetworkObject = runner.Spawn(modelToInstantiate, spawnPos, spawnRot, runner.LocalPlayer);

        if (spawnedNetworkObject != null)
            instantiatedModel = spawnedNetworkObject.GetComponent<MUES_NetworkedTransform>();

        ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Networked model instantiated.", Color.green);
    }

    /// <summary>
    /// Spawns a networked container that will load the GLB model on all clients. For non-master clients, this sends a request to the host to spawn the model.
    /// </summary>
    public void InstantiateFromServer(string modelFileName, Vector3 position, Quaternion rotation, bool makeGrabbable, bool spawnerGrabOnly = false)
    {
        var net = MUES_Networking.Instance;

        if (!net.isConnected)
        {
            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Not connected - cannot instantiate networked models.", Color.yellow);
            return;
        }

        var runner = net.Runner;
        ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Calculated spawn pos: {position}, rot: {rotation.eulerAngles}", Color.cyan);

        if (runner.IsSharedModeMasterClient)
        {
            SpawnModelContainer(modelFileName, makeGrabbable, spawnerGrabOnly, runner.LocalPlayer, position, rotation);
            return;
        }

        Vector3 spawnPos = position;
        Quaternion spawnRot = rotation;

        Transform anchorReference = null;
        
        if (net.isRemote)
        {
            var virtualRoom = MUES_RoomVisualizer.Instance?.virtualRoom;
            if (virtualRoom != null)
            {
                anchorReference = virtualRoom.transform;
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Remote client using virtualRoom as anchor reference at {anchorReference.position}", Color.cyan);
            }
            else
            {
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Remote client has no virtualRoom - cannot calculate anchor-relative position!", Color.red);
                return;
            }
        }
        else
        {
            if (net.sceneParent != null)
            {
                anchorReference = net.sceneParent;
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Colocated client using sceneParent as anchor reference at {anchorReference.position}", Color.cyan);
            }
            else
                ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Colocated client has no sceneParent - sending world position as fallback.", Color.yellow);
        }

        if (anchorReference != null)
        {
            spawnPos = anchorReference.InverseTransformPoint(position);
            spawnRot = Quaternion.Inverse(anchorReference.rotation) * rotation;
            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Client converting spawn: pos {position} -> {spawnPos}, rot {rotation.eulerAngles} -> {spawnRot.eulerAngles}", Color.cyan);
        }

        if (MUES_SessionMeta.Instance != null)
            MUES_SessionMeta.Instance.RequestSpawnModel(modelFileName, makeGrabbable, spawnerGrabOnly, runner.LocalPlayer, spawnPos, spawnRot);
        else
            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] SessionMeta not available - cannot request spawn.", Color.red);
    }

    /// <summary>
    /// Actually spawns the model container. Only called on the master client.
    /// </summary>
    public void SpawnModelContainer(string modelFileName, bool makeGrabbable, bool spawnerGrabOnly, PlayerRef ownerPlayer, Vector3 worldSpawnPos, Quaternion worldSpawnRot)
    {
        var runner = MUES_Networking.Instance.Runner;

        if (!runner.IsSharedModeMasterClient)
        {
            ConsoleMessage.Send(debugMode, "[MUES_ModelManager] Cannot spawn - not master client.", Color.yellow);
            return;
        }

        ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Spawning at world position: {worldSpawnPos}, rotation: {worldSpawnRot.eulerAngles}", Color.cyan);

        var container = runner.Spawn(loadedModelContainer, worldSpawnPos, worldSpawnRot, ownerPlayer,
            onBeforeSpawned: (runner, obj) =>
            {
                var netTransform = obj.GetComponent<MUES_NetworkedTransform>();
                if (netTransform == null) return;

                netTransform.ModelFileName = modelFileName;
                netTransform.SpawnerControlsTransform = spawnerGrabOnly;
                netTransform.IsGrabbable = makeGrabbable;
                netTransform.SpawnerPlayerId = ownerPlayer.PlayerId;

                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] OnBeforeSpawned: Set IsGrabbable={makeGrabbable}, SpawnerControlsTransform={spawnerGrabOnly}, SpawnerPlayerId={ownerPlayer.PlayerId}", Color.cyan);
            }
        );

        container.name = $"ModelContainer_{modelFileName}";

        ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Spawned networked container for: {modelFileName} at {worldSpawnPos} (Owner={ownerPlayer}, SpawnerOnly={spawnerGrabOnly})", Color.green);
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
        string targetDirectory = Path.Combine(Application.persistentDataPath, "Models");
        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        string filePath = Path.Combine(targetDirectory, modelFileName);
        string tempFilePath = filePath + ".tmp";

        if (File.Exists(filePath))
        {
            FileInfo info = new FileInfo(filePath);
            if (info.Length > 0)
            {
                ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Model already cached at: {filePath}", Color.green);
                return filePath;
            }

            ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Cached model is empty/corrupt, deleting: {filePath}", Color.yellow);
            TryDeleteFile(filePath);
        }

        string url = $"{MUES_Networking.Instance.modelDownloadDomain}{modelFileName}";

        using UnityWebRequest uwr = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
        uwr.downloadHandler = new DownloadHandlerFile(tempFilePath);
        var operation = uwr.SendWebRequest();

        while (!operation.isDone)
            await Task.Yield();

        if (uwr.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[MUES_ModelManager] Download failed: {uwr.error}");
            TryDeleteFile(tempFilePath);
            return null;
        }

        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tempFilePath, filePath);
        }
        catch (IOException ex)
        {
            Debug.LogError($"[MUES_ModelManager] Failed to rename temp file to final model path: {ex.Message}");
            return null;
        }

        ConsoleMessage.Send(debugMode, $"[MUES_ModelManager] Model downloaded and saved to: {filePath}", Color.green);
        return filePath;
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
        catch (System.Exception ex)
        {
            Debug.LogError($"[MUES_NetworkedObjectManager] Error releasing semaphore: {ex.Message}");
        }
    }

    private void OnDestroy() => _instantiationSemaphore?.Dispose();
}
