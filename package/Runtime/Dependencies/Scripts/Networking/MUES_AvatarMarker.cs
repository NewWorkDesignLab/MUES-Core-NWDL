using Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using System;
using System.Collections;
using TMPro;
using UnityEngine;

public class MUES_AvatarMarker : MUES_AnchoredNetworkBehaviour
{
    [Tooltip("The unique identifier for this user.")]
    [Networked] public NetworkString<_32> UserGuid { get; set; }
    [Tooltip("The display name of the player.")]
    [Networked] public string PlayerName { get; set; }
 
    [Tooltip("If true, the local player's avatar parts will be destroyed to avoid self-occlusion.")]
    public bool destroyOwnMarker = true;

    [Tooltip("If true, debug messages will be printed to the console.")]
    public bool debugMode;

    [HideInInspector][Networked] public Vector3 HeadLocalPos { get; set; }  // Local position of the head relative to the avatar marker
    [HideInInspector][Networked] public Quaternion HeadLocalRot { get; set; }   // Local rotation of the head relative to the avatar marker

    [HideInInspector][Networked] public Vector3 RightHandLocalPos { get; set; } // Local position of the right hand relative to the avatar marker
    [HideInInspector][Networked] public Vector3 LeftHandLocalPos { get; set; }  // Local position of the left hand relative to the avatar marker

    [HideInInspector][Networked] public Quaternion RightHandLocalRot { get; set; }  // Local rotation of the right hand relative to the avatar marker
    [HideInInspector][Networked] public Quaternion LeftHandLocalRot { get; set; }   // Local rotation of the left hand relative to the avatar marker

    [HideInInspector][Networked] public NetworkBool RightHandVisible { get; set; }  // Visibility state of the right hand marker
    [HideInInspector][Networked] public NetworkBool LeftHandVisible { get; set; }   // Visibility state of the left hand marker

    private Transform mainCam, trackingSpace;   // OVR Camera Rig tracking space
    private Transform head, nameTag, handMarkerRight, handMarkerLeft;   // Avatar parts

    private MeshRenderer headRenderer, handRendererR, handRendererL;    // Renderers for visibility control
    private CanvasGroup nameTagCanvasGroup; // Canvas group for name tag visibility
    private TextMeshProUGUI nameText;   // Text component for displaying player name

    private readonly float rotationSmoothSpeed = 15f;    // Speed for smooth name tag rotation
    private readonly float handSmoothTime = 0.06f;  // Smoothing time for hand marker movement

    private Vector3 rightHandVel, leftHandVel;  // Velocity references for SmoothDamp
    private Quaternion nameSmoothRot, rightHandSmoothRot, leftHandSmoothRot;   // Smoothed rotation for name tag

    private NetworkObject voiceInstance;    // Instance of the voice object for local player

    private bool isReady;   // Flag indicating if the avatar marker is initialized

    /// <summary>
    /// Gets executed when the avatar marker is spawned in the network.
    /// </summary>
    public override void Spawned()
    {
        head = transform.Find("Head");
        headRenderer = head.GetComponent<MeshRenderer>();

        nameTag = head.GetChild(0);
        nameTagCanvasGroup = nameTag.GetComponent<CanvasGroup>();
        nameText = nameTagCanvasGroup.GetComponentInChildren<TextMeshProUGUI>();

        handMarkerRight = transform.Find("HandMarkerR");
        handRendererR = handMarkerRight.GetComponentInChildren<MeshRenderer>();

        handMarkerLeft = transform.Find("HandMarkerL");
        handRendererL = handMarkerLeft.GetComponentInChildren<MeshRenderer>();

        headRenderer.enabled = handRendererR.enabled = handRendererL.enabled = false;
        nameTagCanvasGroup.alpha = 0f;

        StartCoroutine(WaitForComponentInit());
    }

    /// <summary>
    /// Waits for component initialization and sets up the avatar marker based on networked data.
    /// </summary>
    IEnumerator WaitForComponentInit()
    {
        yield return InitAnchorRoutine();

        while (MUES_SessionMeta.Instance == null)
            yield return null;

        var meta = MUES_SessionMeta.Instance;

        if (Object.HasInputAuthority)
        {
            var camPos = Camera.main.transform.position;
            var markerWorldPos = new Vector3(camPos.x, anchor.position.y, camPos.z);
            var markerWorldRot = Quaternion.Euler(0f, Camera.main.transform.eulerAngles.y, 0f);

            transform.SetPositionAndRotation(markerWorldPos, markerWorldRot);
            WorldToAnchor();

            UserGuid = Guid.NewGuid().ToString();

            if (destroyOwnMarker)
            {
                Destroy(head.gameObject);
                Destroy(handMarkerRight.gameObject);
                Destroy(handMarkerLeft.gameObject);
            }
        }
        else
        {
            while (string.IsNullOrEmpty(UserGuid.ToString()))
            {
                ConsoleMessage.Send(debugMode, "Waiting for networked user GUID...", Color.yellow);
                yield return null;
            }
                

            AnchorToWorld();
            ConsoleMessage.Send(debugMode, $"Remote user: Using networked anchor offset/rotation for UserGuid={UserGuid}", Color.green);
        }

        while (trackingSpace == null)
        {
            ConsoleMessage.Send(debugMode, "Waiting for OVR Camera Rig...", Color.yellow);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) trackingSpace = rig.trackingSpace;
            yield return null;
        }

        while (mainCam == null)
        {
            ConsoleMessage.Send(debugMode, "Waiting for Main Camera...", Color.yellow);
            if (Camera.main != null) mainCam = Camera.main.transform;
            yield return null;
        }

        PlatformInit.GetEntitlementInformation(info =>
        {
            if (info.IsEntitled) PlayerName = info.OculusUser.DisplayName;
            else
            {
                PlayerName = "MUES-User";
                ConsoleMessage.Send(debugMode, "User is not entitled to use the application. No username could be fetched.", Color.red);
            }
        });

        SpawnVoiceForLocalPlayer();

        isReady = true;
        headRenderer.enabled = ShouldShowAvatar();

        if (nameTag != null)
        {
            nameSmoothRot = nameTag.rotation;
            nameTagCanvasGroup.alpha = 1f;
        }

        ConsoleMessage.Send(debugMode, "Component Init ready.", Color.green);
    }

    /// <summary>
    /// Spawns the voice object for the local player.
    /// </summary>
    private void SpawnVoiceForLocalPlayer()
    {
        if (voiceInstance != null)
            return;

        var runner = Runner;
        var spawnPos = head != null ? head.position : transform.position;
        var spawnRot = head != null ? head.rotation : transform.rotation;

        voiceInstance = runner.Spawn(MUES_Networking.Instance.voiceObjectPrefab, spawnPos, spawnRot, Object.InputAuthority);
        if (voiceInstance.TryGetComponent<MUES_VoiceObject>(out var voice)) voice.MarkerObjectRef = Object;
    }

    /// <summary>
    /// Gets executed every frame to update the avatar marker's position, rotation, and visibility.
    /// </summary>
    public override void Render()
    {
        if (!isReady) return;
        if (!Object.HasInputAuthority && anchorReady) AnchorToWorld();

        if (mainCam != null && nameTag != null)
        {
            var toCam = mainCam.position - nameTag.position;
            if (toCam.sqrMagnitude > 0.0001f)
            {
                var targetRot = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                nameSmoothRot = Quaternion.Slerp(nameSmoothRot, targetRot, Time.deltaTime * rotationSmoothSpeed);
                nameTag.rotation = nameSmoothRot;
            }

            nameText.text = PlayerName;
        }

        if (head != null)
        {
            var headTargetPos = transform.TransformPoint(HeadLocalPos);
            var headTargetRot = transform.rotation * HeadLocalRot;
            head.SetPositionAndRotation(headTargetPos, headTargetRot);
        }

        if (ShouldShowAvatar())
        {
            if (handMarkerRight != null)
            {
                handRendererR.enabled = RightHandVisible;
                if (RightHandVisible)
                {
                    var handTargetPosR = transform.TransformPoint(RightHandLocalPos);
                    var handSmoothPosR = Vector3.SmoothDamp(handMarkerRight.position, handTargetPosR, ref rightHandVel, handSmoothTime);
                    var handTargetRotR = transform.rotation * RightHandLocalRot;
                    rightHandSmoothRot = Quaternion.Slerp(handMarkerRight.rotation, handTargetRotR, Time.deltaTime * rotationSmoothSpeed);
                    handMarkerRight.SetPositionAndRotation(handSmoothPosR, rightHandSmoothRot);
                }
            }

            if (handMarkerLeft != null)
            {
                handRendererL.enabled = LeftHandVisible;
                if (LeftHandVisible)
                {
                    var handTargetPosL = transform.TransformPoint(LeftHandLocalPos);
                    var handSmoothPosL = Vector3.SmoothDamp(handMarkerLeft.position, handTargetPosL, ref leftHandVel, handSmoothTime);
                    var handTargetRotL = transform.rotation * LeftHandLocalRot;
                    leftHandSmoothRot = Quaternion.Slerp(handMarkerLeft.rotation, handTargetRotL, Time.deltaTime * rotationSmoothSpeed);
                    handMarkerLeft.SetPositionAndRotation(handSmoothPosL, leftHandSmoothRot);
                }
            }
        }
    }

    /// <summary>
    /// Gets executed every physics frame to update the avatar marker's networked data.
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!isReady || trackingSpace == null || !anchorReady) return;

        if (Object.HasInputAuthority) WorldToAnchor();
        else return;

        mainCam.GetPositionAndRotation(out var headWorldPos, out var headWorldRot);

        HeadLocalPos = transform.InverseTransformPoint(headWorldPos);
        HeadLocalRot = Quaternion.Inverse(transform.rotation) * headWorldRot;

        if (ShouldShowAvatar())
        {
            var handTracking =
                OVRInput.IsControllerConnected(OVRInput.Controller.RHand) ||
                OVRInput.IsControllerConnected(OVRInput.Controller.LHand);

            var ctrlR = handTracking ? OVRInput.Controller.RHand : OVRInput.Controller.RTouch;
            var ctrlL = handTracking ? OVRInput.Controller.LHand : OVRInput.Controller.LTouch;

            var rightConnected = OVRInput.IsControllerConnected(ctrlR);
            var leftConnected = OVRInput.IsControllerConnected(ctrlL);

            RightHandVisible = rightConnected;
            LeftHandVisible = leftConnected;

            const float controllerBackOffset = 0.05f;

            if (rightConnected)
            {
                var ctrlLocalPosR = OVRInput.GetLocalControllerPosition(ctrlR);
                var ctrlLocalRotR = OVRInput.GetLocalControllerRotation(ctrlR);

                var ctrlWorldPosR = trackingSpace.TransformPoint(ctrlLocalPosR);
                var ctrlWorldRotR = trackingSpace.rotation * ctrlLocalRotR;

                Quaternion markerWorldRotR;
                Vector3 markerWorldPosR;

                if (handTracking)
                {
                    Vector3 forwardR = ctrlWorldRotR * Vector3.right;
                    Vector3 upR = ctrlWorldRotR * Vector3.up;
                    markerWorldRotR = Quaternion.LookRotation(forwardR, upR);
                    markerWorldPosR = ctrlWorldPosR;
                }
                else
                {
                    markerWorldRotR = ctrlWorldRotR;
                    markerWorldPosR = ctrlWorldPosR + markerWorldRotR * Vector3.back * controllerBackOffset;
                }

                RightHandLocalPos = transform.InverseTransformPoint(markerWorldPosR);
                RightHandLocalRot = Quaternion.Inverse(transform.rotation) * markerWorldRotR;
            }

            if (leftConnected)
            {
                var ctrlLocalPosL = OVRInput.GetLocalControllerPosition(ctrlL);
                var ctrlLocalRotL = OVRInput.GetLocalControllerRotation(ctrlL);

                var ctrlWorldPosL = trackingSpace.TransformPoint(ctrlLocalPosL);
                var ctrlWorldRotL = trackingSpace.rotation * ctrlLocalRotL;

                Quaternion markerWorldRotL;
                Vector3 markerWorldPosL;

                if (handTracking)
                {
                    Vector3 forwardL = ctrlWorldRotL * Vector3.right;
                    Vector3 upL = ctrlWorldRotL * Vector3.up;
                    markerWorldRotL = Quaternion.LookRotation(forwardL, upL);
                    markerWorldPosL = ctrlWorldPosL;
                }
                else
                {
                    markerWorldRotL = ctrlWorldRotL;
                    markerWorldPosL = ctrlWorldPosL + markerWorldRotL * Vector3.back * controllerBackOffset;
                }

                LeftHandLocalPos = transform.InverseTransformPoint(markerWorldPosL);
                LeftHandLocalRot = Quaternion.Inverse(transform.rotation) * markerWorldRotL;
            }
        }
    }

    /// <summary>
    /// Determines whether the avatar should be displayed based on the current networking state.
    /// </summary>
    private bool ShouldShowAvatar() => !MUES_Networking.Instance.isColocated || MUES_Networking.Instance.showAvatarsForColocated;
}
