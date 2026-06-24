namespace PcVolumeControllerDashboard.Core.Audio;

/// <summary>
/// Platform-neutral description of an assignable audio target (a master/mic
/// endpoint, a per-application stream, or a VoiceMeeter strip/bus).
///
/// This is the cross-platform replacement for the WPF host's
/// <c>AudioTargetItem</c>. Unlike that type it carries <b>no</b> backend handle
/// (the old <c>NAudio.CoreAudioApi.AudioSessionControl Session</c> leaked a
/// Windows-only type). Each <see cref="IAudioBackend"/> owns the live handles
/// internally and resolves them from <see cref="Key"/>; callers only ever pass
/// keys back to the backend.
///
/// Targets are addressed by an opaque, stable string <see cref="Key"/>:
///   MASTER          — default render endpoint
///   MIC_INPUT       — default capture endpoint
///   PROC:&lt;name&gt;      — per-application stream(s) (WASAPI sessions / PipeWire sink-inputs)
///   VM_STRIP:&lt;n&gt;     — VoiceMeeter input strip (Windows only)
///   VM_BUS:&lt;n&gt;       — VoiceMeeter output bus (Windows only)
/// Keys are persisted in settings, so their formats must stay stable.
/// </summary>
public sealed class AudioTarget
{
    /// <summary>Stable, opaque key used for all backend operations and persistence.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label shown in the UI (e.g. "chrome", "Strip 1").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Underlying process name, where applicable (empty for endpoints/VM).</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>OS process id, where applicable (0 otherwise).</summary>
    public int ProcessId { get; set; }

    /// <summary>Current volume as a whole percentage 0–100 (display value).</summary>
    public int Volume { get; set; }

    /// <summary>Current mute state.</summary>
    public bool Muted { get; set; }

    /// <summary>Backend-defined state string (e.g. "Active", "Waiting for app").</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>True for the default render (master) endpoint.</summary>
    public bool IsMaster { get; set; }

    /// <summary>True for the default capture (microphone) endpoint.</summary>
    public bool IsMicInput { get; set; }

    /// <summary>True for a VoiceMeeter strip/bus target (Windows only).</summary>
    public bool IsVoiceMeeter { get; set; }

    /// <summary>
    /// True when this target is currently backed by a live, running stream.
    /// Replaces the old "<c>Session != null</c>" test now that the neutral DTO
    /// no longer carries a backend handle.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>True if the target is a master/mic endpoint, VoiceMeeter, or a live stream.</summary>
    public bool IsActiveOrMaster => IsMaster || IsMicInput || IsLive || IsVoiceMeeter;

    public string VolumeDisplay => $"{Volume}%";
    public string MuteDisplay => Muted ? "Yes" : "No";

    public override string ToString() => Label;

    /// <summary>Creates the placeholder target for the default render (master) endpoint.</summary>
    public static AudioTarget CreateMaster() => new()
    {
        Key = "MASTER",
        Label = "Master",
        ProcessName = "System",
        State = "Active",
        IsMaster = true,
    };

    /// <summary>Creates the placeholder target for the default capture (microphone) endpoint.</summary>
    public static AudioTarget CreateMic() => new()
    {
        Key = "MIC_INPUT",
        Label = "Microphone Input",
        State = "Active",
        IsMicInput = true,
    };
}
