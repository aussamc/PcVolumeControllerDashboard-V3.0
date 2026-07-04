using System;
using System.Collections.Generic;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Owns the app's system-wide (global) hotkeys: builds the per-OS
/// <see cref="IGlobalHotkeyService"/>, (re)registers the assigned bindings from
/// settings, and dispatches presses to actions — master volume up/down/mute via the
/// audio backend, and "show dashboard" (raised for the app to honour). Mirrors the
/// WPF host's RegisterAllHotkeys / HandleHotkeyEvent; the WPF-only CycleNextProfile
/// action is descoped from the Avalonia port.
///
/// Master volume/mute changes are reflected on the OLEDs and Audio grid by the
/// dashboard's existing channel-state poll, so no explicit device push is needed.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    // Match the WPF host's IDs (base 0x9000). base+3 (CycleNextProfile) is descoped.
    private const int Base = 0x9000;
    public const int IdMasterVolumeUp = Base + 0;
    public const int IdMasterVolumeDown = Base + 1;
    public const int IdToggleMasterMute = Base + 2;
    public const int IdShowDashboard = Base + 4;

    private const int BaseVolumeStepPercent = 2;
    private const int MaxVolumeStepPercent = 25;
    private const int MaxEncoderSensitivityPercent = 500;

    private readonly IGlobalHotkeyService _service;
    private readonly IAudioBackend _audio;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    /// <summary>Raised (on the UI thread) when the "show dashboard" hotkey fires.</summary>
    public event Action? ShowDashboardRequested;

    /// <summary>
    /// Raised (on the UI thread) after a master volume/mute hotkey changes the audio,
    /// so the on-screen volume overlay reflects hotkey changes just like the encoder.
    /// </summary>
    public event Action<VolumeOverlayInfo>? VolumeChanged;

    public GlobalHotkeyManager(IAudioBackend audio, SettingsService settings, LogService log)
    {
        _audio = audio;
        _settings = settings;
        _log = log;

        _service = CreatePlatformService();
        _service.HotkeyPressed += OnHotkeyPressed;
        SyncFromSettings();
    }

    private static IGlobalHotkeyService CreatePlatformService()
    {
#if WINDOWS
        return new Platform.WindowsGlobalHotkeyService();
#else
        return new NullGlobalHotkeyService();
#endif
    }

    /// <summary>Rebuilds the registered hotkey set from the current settings.</summary>
    public void SyncFromSettings()
    {
        HotkeySettings h = _settings.Settings.Hotkeys;
        var regs = new List<HotkeyRegistration>(4);
        AddIfAssigned(regs, IdMasterVolumeUp, h.MasterVolumeUp);
        AddIfAssigned(regs, IdMasterVolumeDown, h.MasterVolumeDown);
        AddIfAssigned(regs, IdToggleMasterMute, h.ToggleMasterMute);
        AddIfAssigned(regs, IdShowDashboard, h.ShowDashboard);
        _service.RegisterAll(regs);
    }

    private static void AddIfAssigned(List<HotkeyRegistration> list, int id, HotkeyBinding b)
    {
        if (b.IsAssigned)
            list.Add(new HotkeyRegistration(id, b.Modifiers, b.VirtualKey));
    }

    private void OnHotkeyPressed(int id)
    {
        // Fired on the hotkey pump thread — marshal to the UI thread for WASAPI COM
        // affinity and any UI touch.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                switch (id)
                {
                    case IdMasterVolumeUp:
                        _audio.AdjustVolumeByKey("MASTER", MasterStep(), 0, 100);
                        RaiseMasterOverlay();
                        break;
                    case IdMasterVolumeDown:
                        _audio.AdjustVolumeByKey("MASTER", -MasterStep(), 0, 100);
                        RaiseMasterOverlay();
                        break;
                    case IdToggleMasterMute:
                        _audio.ToggleMuteByKey("MASTER");
                        RaiseMasterOverlay();
                        break;
                    case IdShowDashboard:
                        ShowDashboardRequested?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Log($"Hotkey 0x{id:X} error: {ex.Message}");
            }
        });
    }

    private int MasterStep() =>
        EncoderMath.StepFromSensitivity(_settings.Settings.EncoderSensitivityPercent,
            BaseVolumeStepPercent, MaxVolumeStepPercent, MaxEncoderSensitivityPercent);

    /// <summary>Pops the overlay for the current master volume/mute after a hotkey change.</summary>
    private void RaiseMasterOverlay()
    {
        float v = _audio.GetVolumeByKey("MASTER");
        bool muted = _audio.GetMuteByKey("MASTER") ?? false;
        int percent = v < 0f ? 0 : (int)Math.Round(v * 100);
        try { VolumeChanged?.Invoke(new VolumeOverlayInfo(-1, "Master", percent, muted)); }
        catch { /* overlay is best-effort */ }
    }

    public void Dispose()
    {
        _service.HotkeyPressed -= OnHotkeyPressed;
        _service.Dispose();
    }
}
