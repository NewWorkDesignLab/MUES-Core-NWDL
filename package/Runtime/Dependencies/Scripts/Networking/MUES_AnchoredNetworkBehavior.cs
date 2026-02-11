using Fusion;
using System;
using System.Collections;
using UnityEngine;

namespace MUES.Core
{
    public abstract class MUES_AnchoredNetworkBehaviour : NetworkBehaviour
    {
        [HideInInspector][Networked] public Vector3 LocalAnchorOffset { get; set; } // Offset from the anchor in local space
        [HideInInspector][Networked] public Quaternion LocalAnchorRotationOffset { get; set; }  // Offset from the anchor in local space

        [HideInInspector] public bool initialized;  // Indicates if the anchor has been initialized

        private readonly float anchorPositionSmoothTime = 0.35f;    // Smoothing time for position updates
        private readonly float anchorRotationSmoothSpeed = 7f;  // Smoothing speed for rotation updates

        private protected Transform anchor; // The anchor transform to which this object is anchored (sceneParent for both colocated and remote)
        private protected bool anchorReady; // Indicates if the anchor is ready for use

        private Vector3 anchorPosVelocity;  // Velocity used for position smoothing
        private Quaternion anchorSmoothRot; // Smoothed rotation
        private bool anchorSmoothingInitialized;    // Indicates if smoothing has been initialized

        protected const float DefaultTimeout = 7f;    // Default timeout for waiting operations
        protected MUES_Networking Net => MUES_Networking.Instance;

        /// <summary>
        /// Initializes the anchor by waiting for the networking instance and the scene parent to become available.
        /// </summary>
        protected IEnumerator InitAnchorRoutine()
        {
            yield return null;

            if (this == null || gameObject == null)
                yield break;
                
            ConsoleMessage.Send(true, "Starting AnchoredNetworkBehavior init.", Color.green);

            yield return WaitForCondition(() => Net != null, DefaultTimeout);

            if (this == null || gameObject == null)
                yield break;

            if (Net == null)
            {
                ConsoleMessage.Send(true, "Timeout waiting for MUES_Networking.Instance!", Color.red);
                yield break;
            }

            var roomVis = MUES_RoomVisualizer.Instance;

            yield return WaitForCondition(
                () => anchor != null,
                DefaultTimeout,
                () => TryResolveAnchor(Net, roomVis)
            );

            if (this == null || gameObject == null)
                yield break;

            if (anchor == null)
            {
                ConsoleMessage.Send(true, "Timeout waiting for anchor!", Color.red);
                Net?.LeaveRoom();
                yield break;
            }

            transform.SetParent(anchor, true);
            ConsoleMessage.Send(true, $"ANCHORED BEHAVIOR - Parented to {anchor.name} at {anchor.position}", Color.green);

            anchorReady = true;
            ConsoleMessage.Send(true, $"ANCHORED BEHAVIOR - Anchor ready: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.green);
        }

        /// <summary>
        /// Attempts to resolve the anchor transform based on the current networking state.
        /// </summary>
        private void TryResolveAnchor(MUES_Networking net, MUES_RoomVisualizer roomVis)
        {
            if (net.isRemote) TryResolveRemoteAnchor(roomVis);
            else TryResolveLocalAnchor(net);

            if (anchor == null)
                ConsoleMessage.Send(true, $"Waiting for anchor... (isRemote={net.isRemote}, sceneParent={net.sceneParent != null}, anchorTransform={net.anchorTransform != null}, remoteSceneParent={roomVis?.GetRemoteSceneParent() != null})", Color.yellow);
        }

        /// <summary>
        /// Attempts to resolve the anchor for a remote client.
        /// </summary>
        private void TryResolveRemoteAnchor(MUES_RoomVisualizer roomVis)
        {
            var remoteSceneParent = roomVis?.GetRemoteSceneParent();
            if (remoteSceneParent == null) return;

            anchor = remoteSceneParent;
            ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Remote client using REMOTE_SCENE_PARENT as anchor: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.cyan);
        }

        /// <summary>
        /// Attempts to resolve the anchor for a local client.
        /// </summary>
        private void TryResolveLocalAnchor(MUES_Networking net)
        {
            if (net.sceneParent != null)
            {
                anchor = net.sceneParent;
                ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Using sceneParent as anchor: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.cyan);
                return;
            }

            EnsureAnchorTransform(net);

            if (net.anchorTransform != null && net.sceneParent == null)
                net.InitSceneParent();
        }

        /// <summary>
        /// Ensures the anchor transform is set on the networking instance by searching for it if necessary.
        /// </summary>
        private void EnsureAnchorTransform(MUES_Networking net)
        {
            if (net.anchorTransform != null) return;

            var anchorGO = GameObject.FindWithTag("RoomCenterAnchor");
            if (anchorGO == null) return;

            net.anchorTransform = anchorGO.transform;
            ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Found anchor via tag: {net.anchorTransform.name}", Color.cyan);
        }

        /// <summary>
        /// Converts the local anchor offsets to world position and rotation.
        /// </summary>
        protected void AnchorToWorld()
        {
            if (!anchorReady || anchor == null) return;
            if (!TryGetTargetTransform(out var targetPos, out var targetRot)) return;

            bool hasInputAuth = Object != null && Object.IsValid && Object.HasInputAuthority;

            if (hasInputAuth || !anchorSmoothingInitialized)
            {
                ApplyTransformImmediate(targetPos, targetRot);
                return;
            }

            ApplyTransformSmoothed(targetPos, targetRot);
        }

        /// <summary>
        /// Attempts to calculate the target world position and rotation from anchor offsets.
        /// </summary>
        private bool TryGetTargetTransform(out Vector3 targetPos, out Quaternion targetRot)
        {
            targetPos = Vector3.zero;
            targetRot = Quaternion.identity;

            try
            {
                targetPos = anchor.TransformPoint(LocalAnchorOffset);
                targetRot = anchor.rotation * LocalAnchorRotationOffset;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Applies the target position and rotation immediately without smoothing.
        /// </summary>
        private void ApplyTransformImmediate(Vector3 targetPos, Quaternion targetRot)
        {
            transform.SetPositionAndRotation(targetPos, targetRot);
            anchorPosVelocity = Vector3.zero;
            anchorSmoothRot = targetRot;
            anchorSmoothingInitialized = true;
        }

        /// <summary>
        /// Applies the target position and rotation with smoothing for interpolation.
        /// </summary>
        private void ApplyTransformSmoothed(Vector3 targetPos, Quaternion targetRot)
        {
            var smoothedPos = Vector3.SmoothDamp(transform.position, targetPos, ref anchorPosVelocity, anchorPositionSmoothTime);
            anchorSmoothRot = Quaternion.Slerp(anchorSmoothRot, targetRot, Time.deltaTime * anchorRotationSmoothSpeed);
            transform.SetPositionAndRotation(smoothedPos, anchorSmoothRot);
        }

        /// <summary>
        /// Converts the current world position and rotation to local anchor offsets.
        /// </summary>
        protected void WorldToAnchor()
        {
            if (!anchorReady || anchor == null) return;

            try
            {
                transform.GetPositionAndRotation(out var pos, out var rot);
                LocalAnchorOffset = anchor.InverseTransformPoint(pos);
                LocalAnchorRotationOffset = Quaternion.Inverse(anchor.rotation) * rot;
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Public wrapper to force update the anchor offset from current world position.
        /// </summary>
        public void ForceUpdateAnchorOffset() => WorldToAnchor();

        /// <summary>
        /// Waits for a condition to become true within a specified timeout period.
        /// </summary>
        protected IEnumerator WaitForCondition(Func<bool> condition, float timeout, Action onWaiting = null)
        {
            float elapsed = 0f;
            while (!condition() && elapsed < timeout)
            {
                // Check if the object is still valid
                if (this == null || gameObject == null)
                    yield break;
                    
                onWaiting?.Invoke();
                elapsed += Time.deltaTime;
                yield return null;
            }
        }
    }
}