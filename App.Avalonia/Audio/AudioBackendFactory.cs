using System;
using PcVolumeControllerDashboard.Core.Audio;
#if WINDOWS
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Platform.Windows;
#endif

namespace PcVolumeControllerDashboard.App.Audio;

/// <summary>
/// Selects the audio backend for the current OS. The Windows build
/// (net10.0-windows) references Platform.Windows and returns the WASAPI or
/// VoiceMeeter backend; other builds (net10.0) fall back to Core's
/// <see cref="NullAudioBackend"/> until their platform layers land.
/// </summary>
public static class AudioBackendFactory
{
    /// <summary>
    /// Creates the backend for the given mode. <paramref name="mode"/> uses the
    /// Core <c>AudioBackendModes</c> constants ("WASAPI" / "VoiceMeeter").
    /// </summary>
    public static IAudioBackend Create(string mode, Action<string>? logger = null)
    {
#if WINDOWS
        return mode == AudioBackendModes.VoiceMeeter
            ? new VoiceMeeterBackend(logger)
            : new WasapiAudioBackend(logger);
#else
        _ = mode;
        _ = logger;
        return new NullAudioBackend();
#endif
    }
}
