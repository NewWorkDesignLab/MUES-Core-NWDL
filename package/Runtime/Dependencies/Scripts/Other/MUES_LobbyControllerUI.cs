using UnityEngine.UI;
using UnityEngine;
using Oculus.Interaction;

public class MUES_LobbyControllerUI : MonoBehaviour
{
    private PokeInteractable _pokeInteractable; // Poke interactable component
    private CanvasGroup _canvasGroup;   // Canvas group for UI visibility control
    private GameObject mainContainer, loadingContainer; // UI containers
    private Button hostButton, leaveButton; // Host and Leave buttons

    const float frontalOffset = .5f;    // Frontal offset from the camera
    const float heightUnderCamera = .3f;    // Height offset under the camera

    private Transform main => Camera.main.transform;    // Cache main camera transform

    public static MUES_LobbyControllerUI Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
            Instance = this;

        _pokeInteractable = GetComponent<PokeInteractable>();
        _canvasGroup = GetComponent<CanvasGroup>();

        mainContainer = transform.GetChild(0).gameObject;
        loadingContainer = transform.GetChild(1).gameObject;
        loadingContainer.SetActive(false);

        Button[] buttons = mainContainer.GetComponentsInChildren<Button>();

        hostButton = buttons[0];
        leaveButton = buttons[1];

        hostButton.onClick.AddListener(() => MUES_Networking.Instance.InitSharedRoom());
        leaveButton.onClick.AddListener(() => MUES_Networking.Instance.LeaveRoom());
    }

    private void LateUpdate()
    {
        hostButton.enabled = !MUES_Networking.Instance.isConnected;
        leaveButton.enabled = MUES_Networking.Instance.isConnected;

        if (_pokeInteractable.enabled)
        {
            Vector3 camFloorPos = new Vector3(main.position.x, 0, main.position.z);

            transform.position = camFloorPos + (Vector3.ProjectOnPlane(main.forward, Vector3.up) * frontalOffset) +
                new Vector3(0, main.position.y - heightUnderCamera, 0);
        }          
    }

    /// <summary>
    /// Toggles the loading indicator visibility.
    /// </summary>
    public void ShowLoading(bool enabled)
    {
        loadingContainer.SetActive(enabled);
        mainContainer.SetActive(!enabled);
    }
}
