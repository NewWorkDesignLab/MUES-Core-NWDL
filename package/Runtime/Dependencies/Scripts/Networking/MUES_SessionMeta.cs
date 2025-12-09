using Fusion;
using System.Collections.Generic;
using UnityEngine;

public class MUES_SessionMeta : NetworkBehaviour
{
    [Tooltip("A unique identifier for this session used to group anchors.")]
    [Networked] public NetworkString<_64> AnchorGroup { get; set; }
    [Tooltip("The IP address of the host player.")]
    [Networked] public NetworkString<_32> HostIP { get; set; }

    [Tooltip("Whether new players are allowed to join the session.")]
    [Networked] public NetworkBool JoinEnabled { get; set; }

    public static MUES_SessionMeta Instance { get; private set; }

    public override void Spawned() => Instance = this;
}

