using Fusion;
using UnityEngine;
using System.Collections;

#if PHOTON_VOICE_DEFINED
using Photon.Voice.Unity;
#endif

public class MUES_VoiceObject : NetworkBehaviour
{
    [HideInInspector][Networked] public NetworkObject MarkerObjectRef { get; set; } // Reference to the avatar marker object
    private MUES_AvatarMarker marker;    // The avatar marker component

#if PHOTON_VOICE_DEFINED
    private AudioSource _audioSource;   // The audio source for voice playback
    private Recorder _recorder; // The recorder for capturing voice input
    private Speaker _speaker;   // The speaker for playing back received voice

    private VoiceConnection _voiceConnection;   // The Photon Voice connection
#endif

    /// <summary>
    /// Gets called when the voice object is spawned.
    /// </summary>
    public override void Spawned()
    {
#if PHOTON_VOICE_DEFINED
        SetupAudioComponents();
#endif
        StartCoroutine(AttachToAvatarHeadWhenReady());
    }

    /// <summary>
    /// Sets up the audio components for voice communication.
    /// </summary>
#if PHOTON_VOICE_DEFINED
    private void SetupAudioComponents()
    {
        _voiceConnection = FindFirstObjectByType<VoiceConnection>();
        if (_voiceConnection == null)
        {
            ConsoleMessage.Send(true, "[MUES_VoiceObject] No VoiceConnection found in scene.", Color.red);
            return;
        }

        _audioSource = GetComponent<AudioSource>();
        _audioSource.spatialBlend = 1f;
        _audioSource.bypassReverbZones = true;
        _audioSource.playOnAwake = false;
        _audioSource.dopplerLevel = 0f;
        _audioSource.rolloffMode = AudioRolloffMode.Logarithmic;

        _speaker = gameObject.AddComponent<Speaker>();

        _recorder = GetComponent<Recorder>();
        _recorder.SourceType = Recorder.InputSourceType.Microphone;
        _recorder.MicrophoneType = Recorder.MicType.Photon;
        _recorder.RecordWhenJoined = true;
        _recorder.StopRecordingWhenPaused = true;
        _recorder.UseOnAudioFilterRead = false;

        bool isOwner = Object.HasInputAuthority;

        _recorder.RecordingEnabled = isOwner;
        _recorder.TransmitEnabled = isOwner;

        bool shouldPlayLocally = MUES_Networking.Instance == null || !MUES_Networking.Instance.isColocated;
        _audioSource.enabled = shouldPlayLocally;
        _speaker.enabled = shouldPlayLocally;

        _voiceConnection.AddRecorder(_recorder);
    }

    /// <summary>
    /// Gets called when the voice object is despawned.
    /// </summary>
    public override void Despawned(NetworkRunner runner, bool hasStateAuthority)
    {
        if (_voiceConnection != null && _recorder != null) _voiceConnection.RemoveRecorder(_recorder);
    }
#endif

    /// <summary>
    /// Attaches the voice object to the avatar's head when the marker is ready.
    /// </summary>
    /// <returns></returns>
    private IEnumerator AttachToAvatarHeadWhenReady()
    {
        while (!MarkerObjectRef.IsValid)
            yield break;

        NetworkObject markerObj = MarkerObjectRef;
        marker = markerObj != null ? markerObj.GetComponent<MUES_AvatarMarker>() : null;

        if (marker == null)
        {
            ConsoleMessage.Send(true, "[MUES_VoiceObject] Avatar marker not found for voice object.", Color.red);
            yield break;
        }         

        Transform head = marker.transform.Find("Head");
        Transform target = head != null ? head : marker.transform;

        if (!marker.Object.HasInputAuthority)
        {
            transform.SetParent(target);
            transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }
        else transform.SetPositionAndRotation(target.position, target.rotation);
    }
}
