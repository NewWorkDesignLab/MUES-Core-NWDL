using Fusion;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Fusion.Sockets.NetBitBuffer;

public class MUES_UI : MonoBehaviour
{
    [Header("Containers")]
    [Tooltip("Container shown during normal operation")]
    public GameObject containerMain;
    [Tooltip("Container shown when user is prompted to enter scan mode")]
    public GameObject containerEnterScan;
    [Tooltip("Container shown when user is loading")]
    public GameObject containerLoading;
    [Tooltip("Container shown when advice is given to the user")]
    public GameObject containerAdvice;
    [Tooltip("Container shown when a device is joined")]
    public GameObject containerJoined;
    [Tooltip("Container shown when a device is disconnected")]
    public GameObject containerDisconnected;
    [Tooltip("Container shown when the user is prompted to rescan")]
    public GameObject containerRescan;

    [Header("Player List UI")]
    [Tooltip("Prefab for player buttons in the player list UI")]
    public GameObject playerButtonPrefab; 
    [Tooltip("Parent RectTransform for player buttons")]
    public RectTransform contentParent;

    [Header("Networked Objects")]
    [Tooltip("Networked Transform for the blue cube. (Everybody can grab)")]
    public MUES_NetworkedTransform cubeBlue; 
    [Tooltip("Networked Transform for the red cube. (Only spawner can grab)")]
    public MUES_NetworkedTransform cubeRed;

    private GameObject currentContainer;    // Currently active container

    private Button disconnectButton;    // Button to disconnect from the room
    private Button hostButton, joinButton;  // Buttons in the main container

    private InputField codeInput;   // Input field for entering room code
    private Button codeSubmitButton;    // Button to submit room code

    private Dictionary<PlayerRef, GameObject> playerButtons = new();  // Stores references to player buttons by PlayerRef
    private List<Button> playerKickButtons = new(); // Stores references to player control buttons

    private Button spawnButton; // Button to spawn an object
    private MUES_NetworkedTransform objectToSpawn; // The networked object to spawn
    private TextMeshProUGUI codeDisplayText;    // Text to display the room code

    private Button okButtonDisconnected, okButtonRescan;    // OK buttons in various containers
    private Button rescanButton;    // Rescan button in the rescan container

    private TextMeshProUGUI adviceText; // Text for advice container

    void Start()
    {
        disconnectButton = transform.GetChild(0).GetChild(0).GetComponentInChildren<Button>();
        disconnectButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.LeaveRoom();
            SwitchToContainer(containerDisconnected);
        });
        disconnectButton.interactable = false;

        adviceText = containerAdvice.GetComponentInChildren<TextMeshProUGUI>();

        SetupMainContainer();
        SetupEnterScanContainer();
        SetupJoinedContainer(); 
        SetupDisconnectedContainer();
        SetupRescanContainer();
    }

    private void OnEnable()
    {
        MUES_Networking.Instance.OnLobbyCreationStarted += () =>
        {
            SwitchToContainer(containerLoading);
        };

        MUES_Networking.Instance.OnRoomMeshLoadFailed += () =>
        {
            SwitchToContainer(containerRescan);
        };

        MUES_Networking.Instance.OnRoomCreationFailed += () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        MUES_Networking.Instance.OnRoomCreatedSuccessfully += (roomCode) =>
        {
            codeDisplayText.text = $"Lobby Code: {roomCode}";
        };

        MUES_Networking.Instance.OnPlayerJoined += (playerRef) =>
        {
            if (MUES_Networking.Instance.Runner != null && playerRef != MUES_Networking.Instance.Runner.LocalPlayer)
                AddPlayerToList(playerRef);

            UpdateJoinContainerPermissions();
            SwitchToContainer(containerJoined);
        };

        MUES_Networking.Instance.OnPlayerLeft += (playerRef) =>
        {
            RemovePlayerFromList(playerRef);
        };

        MUES_Networking.Instance.OnBecameMasterClient += () =>
        {
            UpdateJoinContainerPermissions();
        };

        MUES_Networking.Instance.OnRoomLeft += () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        MUES_RoomVisualizer.Instance.OnChairPlacementStarted += () =>
        {
            adviceText.text = "Press the PRIMARY TRIGGER to place a chair.\nPress A to end the chair placement.";
            SwitchToContainer(containerAdvice);
        };

        MUES_RoomVisualizer.Instance.OnChairPlacementEnded += () =>
        {
            SwitchToContainer(containerLoading);
        };
    }

    private void OnDisable()
    {
        MUES_Networking.Instance.OnLobbyCreationStarted -= () =>
        {
            SwitchToContainer(containerLoading);
        };

        MUES_Networking.Instance.OnRoomMeshLoadFailed -= () =>
        {
            SwitchToContainer(containerRescan);
        };

        MUES_Networking.Instance.OnRoomCreationFailed -= () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        MUES_Networking.Instance.OnRoomCreatedSuccessfully -= (roomCode) =>
        {
            codeDisplayText.text = $"Lobby Code: {roomCode}";
        };

        MUES_Networking.Instance.OnPlayerJoined -= (playerRef) =>
        {
            if (MUES_Networking.Instance.Runner != null && playerRef != MUES_Networking.Instance.Runner.LocalPlayer)
                AddPlayerToList(playerRef);

            UpdateJoinContainerPermissions();
            SwitchToContainer(containerJoined);
        };

        MUES_Networking.Instance.OnPlayerLeft -= (playerRef) =>
        {
            RemovePlayerFromList(playerRef);
        };

        MUES_Networking.Instance.OnBecameMasterClient -= () =>
        {
            UpdateJoinContainerPermissions();
        };

        MUES_Networking.Instance.OnRoomLeft -= () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        MUES_RoomVisualizer.Instance.OnChairPlacementStarted -= () =>
        {
            adviceText.text = "Press the PRIMARY TRIGGER to place a chair.\nPress A to end the chair placement.";
            SwitchToContainer(containerAdvice);
        };

        MUES_RoomVisualizer.Instance.OnChairPlacementEnded -= () =>
        {
            SwitchToContainer(containerLoading);
        };
    }

    void Update()
    {
        ManageDisplayPosition(transform);
        disconnectButton.interactable = MUES_Networking.Instance.isConnected;
    }

    /// <summary>
    /// Manages the position of the display UI to stay in front of the user.
    /// </summary>
    public void ManageDisplayPosition(Transform displayTransform)
    {
        if (Camera.main == null)
            return;

        Vector3 forwardProjected = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
        Vector3 targetPosition = Camera.main.transform.position + forwardProjected * 2f;
        targetPosition.y = Camera.main.transform.position.y - 5f;

        displayTransform.position = Vector3.Lerp(displayTransform.position, targetPosition, Time.deltaTime * .5f);
    }

    #region Container Setups

    /// <summary>
    /// Sets up the main container UI and button functionality.
    /// </summary>
    void SetupMainContainer()
    {
        Button[] buttons = containerMain.GetComponentsInChildren<Button>();

        hostButton = buttons[0];
        joinButton = buttons[1];

        hostButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.StartLobbyCreation();
            adviceText.text = "Try to view as much of the room! (until the reticle turns green / yellow)";
            SwitchToContainer(containerAdvice);
        });

        joinButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.EnableQRCodeScanning();
            SwitchToContainer(containerEnterScan);
        });
    }

    /// <summary>
    /// Sets up the enter scan container UI and button functionality.
    /// </summary>
    void SetupEnterScanContainer()
    {
        codeInput = containerEnterScan.GetComponentInChildren<InputField>();
        codeSubmitButton = codeInput.GetComponentInChildren<Button>();

        codeSubmitButton.onClick.AddListener(() =>
        {
            string code = codeInput.text;

            if (!string.IsNullOrEmpty(code))
            {
                MUES_Networking.Instance.JoinSessionFromCode(code);
                SwitchToContainer(containerLoading);
            }
        });
    }

    /// <summary>
    /// Sets up the joined container UI.
    /// </summary>
    void SetupJoinedContainer()
    {
        codeDisplayText = containerJoined.GetComponentInChildren<TextMeshProUGUI>();

        spawnButton = containerJoined.GetComponentInChildren<Button>();
        spawnButton.onClick.AddListener(() =>
        {
            var (position, rotation) = GetSpawnPoseInFrontOfCamera(1.0f);
            MUES_NetworkedObjectManager.Instance.Instantiate(objectToSpawn, position, rotation, out _);
        });
    }

    /// <summary>
    /// Sets up the disconnected container UI and button functionality.
    /// </summary>
    void SetupDisconnectedContainer()
    {
        okButtonDisconnected = containerDisconnected.GetComponentInChildren<Button>();
        okButtonDisconnected.onClick.AddListener(() =>
        {
            SwitchToContainer(containerMain);
        });
    }

    /// <summary>
    /// Sets up the rescan container UI and button functionality.
    /// </summary>
    void SetupRescanContainer()
    {
        Button[] buttons = containerMain.GetComponentsInChildren<Button>();

        rescanButton = buttons[0];
        okButtonRescan = buttons[1];

        rescanButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.LaunchSpaceSetup();
            SwitchToContainer(containerMain);
        });

        okButtonRescan.onClick.AddListener(() =>
        {
            SwitchToContainer(containerMain);
        });
    }

    #endregion

    #region Container Switching and Player List Management

    /// <summary>
    /// Switches the active UI container to the specified new container.
    /// </summary>
    void SwitchToContainer(GameObject newContainer)
    {
        currentContainer.SetActive(false);

        currentContainer = newContainer;
        currentContainer.SetActive(true);
    }

    /// <summary>
    /// Updates the permissions of the join container based on whether the local player is the master client.
    /// </summary>
    void UpdateJoinContainerPermissions()
    {
        bool isMasterClient = MUES_Networking.Instance.Runner != null && MUES_Networking.Instance.Runner.IsSharedModeMasterClient;

        objectToSpawn = isMasterClient ? cubeRed : cubeBlue;    
        spawnButton.GetComponentInChildren<TextMeshProUGUI>().text = isMasterClient ? $"Spawn\r\n<color=red>Red</color>\r\nCube" : $"Spawn\r\n<color=blue>Blue</color>\r\nCube";

        playerKickButtons.RemoveAll(button => button == null);
                
        foreach (var button in playerKickButtons)
        {
            if (button != null)
                button.interactable = isMasterClient;
        }
    }

    /// <summary>
    /// Creates a button for a player in the UI list.
    /// </summary>
    void CreateButton(PlayerInfo player)
    {
        var button = Instantiate(playerButtonPrefab, contentParent);
        playerButtons[player.PlayerRef] = button;

        button.GetComponentInChildren<TextMeshProUGUI>().text = player.PlayerName.ToString();

        Button muteButton = button.transform.Find("MuteButton").GetComponent<Button>();
        muteButton.onClick.AddListener(() => { MUES_Networking.Instance.ToggleMutePlayer(player.PlayerRef); ToggleMuteVisual(muteButton, player.PlayerRef); });

        Button kickButton = button.transform.Find("KickButton").GetComponent<Button>();
        kickButton.onClick.AddListener(() => { MUES_Networking.Instance.KickPlayer(player.PlayerRef); });

        kickButton.interactable = MUES_Networking.Instance.Runner.IsSharedModeMasterClient;
        playerKickButtons.Add(kickButton);
    }

    /// <summary>
    /// Function to add a player to the UI list when they join. (Call in OnPlayerJoined callback in MUES_NetworkingEvents)  
    /// </summary>
    public void AddPlayerToList(PlayerRef player)
    {
        PlayerInfo? playerInfo = MUES_Networking.Instance.GetPlayer(player);
        if (playerInfo.HasValue) CreateButton(playerInfo.Value);
    }

    /// <summary>
    /// Function to remove a player from the UI list when they leave. (Call in OnPlayerLeft callback in MUES_NetworkingEvents)
    /// </summary>
    public void RemovePlayerFromList(PlayerRef player)
    {
        if (playerButtons.TryGetValue(player, out var buttonObj))
        {
            if (buttonObj != null)
            {
                Button kickButton = buttonObj.transform.Find("KickButton")?.GetComponent<Button>();
                if (kickButton != null)
                    playerKickButtons.Remove(kickButton);
            }
            
            Destroy(buttonObj);
            playerButtons.Remove(player);
        }
    }

    /// <summary>
    /// Example function to toggle mute visual state on the mute button.
    /// </summary>
    void ToggleMuteVisual(Button button, PlayerRef playerRef)
    {
        bool isMuted = MUES_Networking.Instance.IsPlayerLocallyMuted(playerRef);

        var muteButtonImage = button.image;
        if (muteButtonImage != null) muteButtonImage.color = isMuted ? Color.red : Color.gray;
    }


    /// <summary>
    /// Gets a spawn position and rotation in front of the main camera.
    /// </summary>
    private (Vector3 position, Quaternion rotation) GetSpawnPoseInFrontOfCamera(float distance)
    {
        Vector3 camPos = Camera.main.transform.position;
        Vector3 flatForward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;

        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;

        return (camPos + flatForward * distance, Quaternion.LookRotation(flatForward, Vector3.up));
    }

    #endregion
}
