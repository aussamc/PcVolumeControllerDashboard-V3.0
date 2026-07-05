using System;
using System.Runtime.InteropServices;
using PcVolumeControllerDashboard.Core.Audio;
#if WINDOWS
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Platform.Windows;
#else
using PcVolumeControllerDashboard.Platform.Linux;
#endif

namespace PcVolumeControllerDashboard.App.Audio;

/// <summary>
/// Selects the audio backend for the current OS. The Windows build
/// (net10.0-windows) references Platform.Windows and returns the WASAPI or
/// VoiceMeeter backend. The net10.0 build (Linux + macOS share this TFM — there's
/// no -linux TFM suffix in .NET) picks between Platform.Linux's
/// <see cref="PipeWireAudioBackend"/> and Core's <see cref="NullAudioBackend"/> at
/// runtime, following the same <see cref="RuntimeInformation.IsOSPlatform"/>
/// pattern already used for OS branching elsewhere (e.g.
/// <c>MainWindow.OpenInFileManager</c>).
/// </summary>
public static class AudioBackendFactory
{
    /// <summary>
    /// Creates the backend for the given mode. <paramref name="mode"/> uses the
    /// Core <c>AudioBackendModes</c> constants ("WASAPI" / "VoiceMeeter") on
    /// Windows; it's ignored on other platforms, which have exactly one backend.
    /// </summary>
    public static IAudioBackend Create(string mode, Action<string>? logger = null)
    {
#if WINDOWS
        return mode == AudioBackendModes.VoiceMeeter
            ? new VoiceMeeterBackend(logger)
            : new WasapiAudioBackend(logger);
#else
        _ = mode;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new PipeWireAudioBackend(logger);
        return new NullAudioBackend();
#endif
    }
}
