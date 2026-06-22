namespace PcVolumeControllerDashboard.Core.Audio;

/// <summary>
/// Platform-neutral abstraction over an audio routing backend. Implementations:
///   • <c>WasapiAudioBackend</c>   — Windows WASAPI (NAudio)          [Platform.Windows]
///   • <c>VoiceMeeterBackend</c>   — VoiceMeeter Remote API           [Platform.Windows]
///   • (future) PipeWire/wpctl     — Linux per-app sink-inputs        [Platform.Linux]
///   • (future) CoreAudio          — macOS master volume              [Platform.Mac]
///
/// All operations are addressed by the opaque <see cref="AudioTarget.Key"/>, so
/// the host never touches a backend-specific handle. The contract is designed
/// against BOTH known backends (WASAPI sessions and VoiceMeeter parameters) and
/// the planned Linux <c>wpctl</c> shell-out, whose stream IDs are ephemeral —
/// hence enumeration is always dynamic (<see cref="GetAvailableTargets"/>) and
/// keys are stable strings, never live IDs.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>Human-readable backend name shown in diagnostics (e.g. "WASAPI", "VoiceMeeter").</summary>
    string BackendName { get; }

    /// <summary>True when the backend is reachable and responding to API calls.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fired (on any thread) when <see cref="IsAvailable"/> flips. Subscribers
    /// must marshal to the UI thread as required.
    /// </summary>
    event Action? AvailabilityChanged;

    /// <summary>
    /// Fired (on any thread) when the set of available targets may have changed
    /// (default device switch, app started/stopped streaming). The host should
    /// re-enumerate and refresh. Subscribers must marshal to the UI thread.
    /// </summary>
    event Action? TargetsChanged;

    /// <summary>Initialises the backend. Must be called once before any other member.</summary>
    void Initialise();

    /// <summary>
    /// Returns the current list of assignable targets. Re-enumerated dynamically;
    /// the returned keys are valid inputs to the other methods.
    /// </summary>
    IReadOnlyList<AudioTarget> GetAvailableTargets();

    /// <summary>Current normalised volume (0–1) for the key, or −1 on failure.</summary>
    float GetVolumeByKey(string key);

    /// <summary>Sets the absolute normalised volume (0–1) for the key. False on failure.</summary>
    bool SetVolumeByKey(string key, float normalizedVolume);

    /// <summary>
    /// Applies a relative volume change to the key, clamped to
    /// [<paramref name="minPercent"/>, <paramref name="maxPercent"/>]. Where a key
    /// maps to several underlying streams, the delta is applied to each
    /// independently (matching the WPF host's per-session behaviour). Returns the
    /// representative new volume as a whole percentage, or −1 on failure.
    ///
    /// This relative primitive suits the physical rotary encoder (which emits
    /// deltas) and maps cleanly onto both WASAPI per-session writes and
    /// <c>wpctl set-volume &lt;id&gt; &lt;d&gt;%+/-</c>.
    /// </summary>
    int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent);

    /// <summary>Current mute state for the key, or null on failure.</summary>
    bool? GetMuteByKey(string key);

    /// <summary>Sets the mute state for the key. False on failure.</summary>
    bool SetMuteByKey(string key, bool mute);

    /// <summary>
    /// Toggles mute for the key and returns the new state, or null on failure.
    /// Where a key maps to several streams, the first stream decides the new
    /// state and all streams are set to it (matching the WPF host).
    /// </summary>
    bool? ToggleMuteByKey(string key);

    /// <summary>
    /// Forces the next <see cref="GetAvailableTargets"/> / volume query to
    /// re-enumerate from the OS rather than returning a cached result. Call after
    /// a known device/stream change or an explicit user refresh.
    /// </summary>
    void InvalidateCache();
}
