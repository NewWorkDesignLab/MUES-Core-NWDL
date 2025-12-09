using Fusion;
using System;
using System.Collections;
using UnityEngine;

public class MUES_NetworkedObject : MUES_AnchoredNetworkBehaviour
{
    [Tooltip("The unique identifier for this networked object.")]
    [Networked] public NetworkString<_32> ObjectGuid { get; set; }

    private bool isReady;   // Flag indicating if the object has been initialized

    /// <summary>
    /// Gets executed when the object is spawned in the network.
    /// </summary>
    public override void Spawned() => StartCoroutine(InitRoutine());

    /// <summary>
    /// Gets the anchor and initializes the object's position and rotation based on networked data.
    /// </summary>
    IEnumerator InitRoutine()
    {
        yield return InitAnchorRoutine();

        if (Object.HasStateAuthority)
        {
            while (MUES_SessionMeta.Instance == null)
            {
                ConsoleMessage.Send(true, "Waiting for session meta...", Color.yellow);
                yield return null;
            }

            ObjectGuid = Guid.NewGuid().ToString();
            WorldToAnchor();
        }
        else
        {
            while (string.IsNullOrEmpty(ObjectGuid.ToString()))
            {
                ConsoleMessage.Send(true, "Waiting for object GUID...", Color.yellow);
                yield return null;
            }               

            AnchorToWorld();
        }

        isReady = true;
    }

    /// <summary>
    /// Gets executed every frame to update the object's position and rotation based on anchor data.
    /// </summary>
    public override void Render()
    {
        if (!Object.HasStateAuthority && isReady) AnchorToWorld();
    }

    /// <summary>
    /// Gets executed at fixed network intervals to update the networked anchor offsets.
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority || !isReady || !anchorReady) return;
        WorldToAnchor();
    }
}
