namespace PcVolumeControllerDashboard;

/// <summary>
/// Abstraction over audio routing backends (WASAPI and VoiceMeeter).
/// Provides key-based volume/mute operations and target enumeration so the
/// dashboard can route encoder events to either backend without knowing
/// the underlying API details.
/// </summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>Human-readable name shown in diagnostics (e.g. "WASAPI", "VoiceMeeter").</summary>
    string BackendName { get; }

    /// <summary>True when the backend is reachable and responding to API calls.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fired (on any thread) when <see cref="IsAvailable"/> transitions between
    /// true and false.  Subscribers must marshal to the UI thread as required.
    /// </summary>
    event Action? AvailabilityChanged;

    /// <summary>
    /// Initialises the backend.  Must be called once before any other member.
    /// </summary>
    void Initialise();

    /// <summary>
    /// Returns the current list of assignable audio targets.
    /// The returned items have keys suitable for passing to the other methods.
    /// </summary>
    List<AudioTargetItem> GetAvailableTargets();

    /// <summary>
    /// Returns the current normalised volume (0–1) for the given target key.
    /// Returns −1 on failure (target not found, backend unavailable, etc.).
    /// </summary>
    float GetVolumeByKey(string targetKey);

    /// <summary>
    /// Sets the normalised volume (0–1) for the given target key.
    /// Returns false on failure.
    /// </summary>
    bool SetVolumeByKey(string targetKey, float normalizedVolume);

    /// <summary>
    /// Returns the current mute state for the given target key.
    /// Returns null on failure.
    /// </summary>
    bool? GetMuteByKey(string targetKey);

    /// <summary>
    /// Sets the mute state for the given target key.
    /// Returns false on failure.
    /// </summary>
    bool SetMuteByKey(string targetKey, bool mute);
}
