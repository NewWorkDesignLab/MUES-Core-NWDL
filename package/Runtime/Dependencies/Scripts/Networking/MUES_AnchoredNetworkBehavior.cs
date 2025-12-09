using Fusion;
using System.Collections;
using UnityEngine;

public abstract class MUES_AnchoredNetworkBehaviour : NetworkBehaviour
{
    [Tooltip("The local position offset from the room center anchor.")]
    [Networked] public Vector3 LocalAnchorOffset { get; set; }

    [Tooltip("The local rotation offset from the room center anchor.")]
    [Networked] public Quaternion LocalAnchorRotationOffset { get; set; }

    [Tooltip("Whether to smoothly interpolate position and rotation when applying anchor transforms.")]
    public bool useAnchorSmoothing = false;

    private readonly float anchorPositionSmoothTime = 0.06f;    // Smoothing time for position
    private readonly float anchorRotationSmoothSpeed = 15f; // Smoothing speed for rotation

    private protected Transform anchor; // The room center anchor transform
    private protected bool anchorReady; // Flag indicating if the anchor is ready

    private Vector3 anchorPosVelocity;  // Velocity used for position smoothing
    private Quaternion anchorSmoothRot; // Smoothed rotation
    private bool anchorSmoothingInitialized;    // Flag indicating if smoothing has been initialized

    /// <summary>
    /// Initializes the anchor by waiting for the networking instance and the room center anchor to become available.
    /// </summary>
    protected IEnumerator InitAnchorRoutine()
    {
        while (MUES_Networking.Instance == null)
            yield return null;

        var net = MUES_Networking.Instance;

        while (anchor == null)
        {
            ConsoleMessage.Send(true, "Waiting for room center anchor...", Color.yellow);
            anchor = net.isColocated ? MUES_Networking.GetRoomCenterAnchor() : MUES_Networking.GetRoomCenter();
            yield return null;
        }

        anchorReady = true;
    }

    /// <summary>
    /// Converts the current world position and rotation to local anchor offsets.
    /// </summary>
    protected void WorldToAnchor()
    {
        if (!anchorReady) return;

        transform.GetPositionAndRotation(out var pos, out var rot);
        LocalAnchorOffset = anchor.InverseTransformPoint(pos);
        LocalAnchorRotationOffset = Quaternion.Inverse(anchor.rotation) * rot;
    }

    /// <summary>
    /// Converts the local anchor offsets to world position and rotation.
    /// </summary>
    protected void AnchorToWorld()
    {
        if (!anchorReady) return;

        var targetPos = anchor.TransformPoint(LocalAnchorOffset);
        var targetRot = anchor.rotation * LocalAnchorRotationOffset;

        if (!useAnchorSmoothing)
        {
            transform.SetPositionAndRotation(targetPos, targetRot);
            return;
        }

        if (!anchorSmoothingInitialized)
        {
            transform.SetPositionAndRotation(targetPos, targetRot);
            anchorSmoothRot = targetRot;
            anchorSmoothingInitialized = true;
            return;
        }

        var smoothedPos = Vector3.SmoothDamp(transform.position,targetPos,ref anchorPosVelocity,anchorPositionSmoothTime);
        anchorSmoothRot = Quaternion.Slerp(anchorSmoothRot,targetRot,Time.deltaTime * anchorRotationSmoothSpeed);
        transform.SetPositionAndRotation(smoothedPos, anchorSmoothRot);
    }
}
