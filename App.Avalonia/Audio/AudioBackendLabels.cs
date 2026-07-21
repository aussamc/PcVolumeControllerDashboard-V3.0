using System;

namespace PcVolumeControllerDashboard.App.Audio;

/// <summary>
/// User-facing naming for the audio-backend choice, which is the one setting whose
/// vocabulary is genuinely platform-specific: the same "system audio" radio means
/// WASAPI on Windows and PipeWire on Linux, and VoiceMeeter exists only on Windows.
///
/// Kept out of the XAML (which has no way to branch on OS) and out of
/// <see cref="AudioBackendFactory"/> (which picks implementations, not words) so the
/// wizard page and the Setup tab can't drift apart in what they call the same thing.
/// Runtime checks rather than <c>#if WINDOWS</c> because both TFMs need all three
/// strings compiled in — the labels are also what the Setup tab shows when explaining
/// why a control is disabled.
/// </summary>
internal static class AudioBackendLabels
{
    /// <summary>Whether VoiceMeeter is selectable at all — it ships as a Windows-only API.</summary>
    public static bool VoiceMeeterSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Label for the non-VoiceMeeter radio: names the concrete backend the current OS
    /// will actually use, so the choice isn't described in another platform's terms.
    /// </summary>
    public static string SystemBackend =>
        OperatingSystem.IsWindows() ? "Windows audio (WASAPI)"
        : OperatingSystem.IsLinux() ? "Linux audio (PipeWire)"
        : "System audio";

    /// <summary>Short form for the Setup tab's denser radio row.</summary>
    public static string SystemBackendShort =>
        OperatingSystem.IsWindows() ? "WASAPI (Windows audio sessions)"
        : OperatingSystem.IsLinux() ? "PipeWire (Linux audio streams)"
        : "System audio";

    /// <summary>Explanatory line above the radios, matched to what this OS can offer.</summary>
    public static string ChoiceDescription =>
        VoiceMeeterSupported
            ? "Choose how volume is routed. WASAPI works for most setups; pick VoiceMeeter only if you use it."
            : $"Volume is routed through {SystemBackend}. VoiceMeeter is a Windows-only application, so it isn't available here.";

    /// <summary>
    /// Note shown under the radios. On Windows it's guidance; elsewhere it explains the
    /// disabled control, so a greyed-out option never reads as a bug.
    /// </summary>
    public static string VoiceMeeterNote =>
        VoiceMeeterSupported
            ? "VoiceMeeter is Windows-only."
            : "VoiceMeeter requires Windows.";
}
