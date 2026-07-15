namespace PcVolumeControllerDashboard.Core;

public sealed class HotkeyBinding
{
    public bool Enabled { get; set; }
    public int Modifiers { get; set; }   // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public int VirtualKey { get; set; }  // Win32 virtual-key code; 0 = unassigned

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAssigned => Enabled && VirtualKey != 0;

    // Note: the human-readable form (Ctrl+Shift+K etc.) is produced by the host's
    // HotkeyBindingExtensions.ToDisplayString — it depends on System.Windows.Forms.Keys
    // for key naming and therefore cannot live in the platform-agnostic Core.
}

public sealed class HotkeySettings
{
    public HotkeyBinding MasterVolumeUp   { get; set; } = new();
    public HotkeyBinding MasterVolumeDown { get; set; } = new();
    public HotkeyBinding ToggleMasterMute { get; set; } = new();
    public HotkeyBinding CycleNextProfile { get; set; } = new();
    public HotkeyBinding ShowDashboard    { get; set; } = new();
}

/// <summary>
/// A named set of channel assignments that can be switched at runtime.
/// Profiles store the six channel settings; all other dashboard settings are global.
/// </summary>
public sealed class ProfileEntry
{
    public string Name { get; set; } = "Default";
    public ChannelSettings[] Channels { get; set; } = DashboardSettings.CreateDefaultChannels();
}

public sealed class DashboardSettings
{
    // Incremented whenever a migration runs in NormalizeSettings so future
    // migrations can be gated on the previous version number.
    public int SettingsVersion { get; set; } = 0;

    public string LastComPort { get; set; } = string.Empty;

    // Chip ID of the last successfully paired ESP32 controller.
    // Empty = no controller has been paired yet (first connection auto-pairs).
    // Non-empty = only controllers reporting this chip ID are recognised without a warning.
    public string LastDeviceChipId { get; set; } = string.Empty;
    public bool AutoConnectOnLaunch { get; set; } = true;
    public bool FirstRunWizardCompleted { get; set; } = true;
    public bool ScanAllComPortsIfRememberedMissing { get; set; } = true;
    // Tray/login defaults default ON for a fresh install (v3.16): this is a background
    // tray utility, so new users almost always want it tucked into the tray and launched
    // with the OS. Fresh-install only — Normalize must never retroactively flip these for
    // existing users' saved settings.json.
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool AdvancedDebugLogging { get; set; }
    public bool TrayNotificationsEnabled { get; set; } = true;

    // Advanced Debug Features (parity item D2): shows/hides the entire Debug tab
    // (serial console, hardware self-test, and diagnostics readout). Default off so
    // the developer/troubleshooting surface stays out of the way; the `--debug`
    // startup flag force-shows it for a single session regardless of this setting.
    public bool AdvancedDebugFeatures { get; set; }
    public int SelectedChannelIndex { get; set; }

    public int EncoderSensitivityPercent { get; set; } = 50;

    // Encoder Feel — acceleration and smoothing (added v2.16)
    // AccelerationEnabled defaults ON (v3.16, Medium preset): Medium acceleration gives a
    // better out-of-box feel (fast spins cover the range quickly, slow turns stay precise).
    // VolumeSmoothingEnabled stays off. Fresh-install only. The ChannelRuntime/EncoderMath
    // path already honours this flag, so this is a pure default change, not new behaviour.
    public bool AccelerationEnabled { get; set; } = true;
    public string AccelerationPreset { get; set; } = AccelerationPresets.Medium;
    public bool VolumeSmoothingEnabled { get; set; } = false;
    public string VolumeSmoothingSpeed { get; set; } = SmoothingSpeed.Normal;

    // Custom acceleration curve (used when AccelerationPreset == "Custom")
    // AccelThresholdMs   — encoder interval (ms) below which full acceleration applies;
    //                      higher = boost activates at slower turning speeds.
    // AccelMaxMultiplier — step multiplier at maximum speed (interval ≈ 0 ms).
    // AccelCurveExponent — shape of the ramp; < 1 = early kick-in, 1 = linear, > 1 = late.
    public int   AccelThresholdMs     { get; set; } = 150;
    public float AccelMaxMultiplier   { get; set; } = 8.0f;
    public float AccelCurveExponent   { get; set; } = 0.5f;

    public string ThemeMode { get; set; } = ThemeModes.FollowSystem;
    // Default OLED mode is Large Volume Number (v3.16). Fresh-install only; existing
    // users keep their saved mode (Normalize only fills a blank one, see SettingsRepository).
    public string OledDisplayMode { get; set; } = DisplayModes.LargeVolume;
    public int OledBrightnessPercent { get; set; } = 100;
    public int OledSleepTimeoutMinutes { get; set; } = 2;
    public string OledConnectedIdleAction { get; set; } = OledIdleActions.DimTo30;
    public int OledConnectedIdleTimeoutMinutes { get; set; } = 10;
    public bool OledAntiBurnInEnabled { get; set; } = true;

    public double WindowWidth { get; set; } = 1300;
    public double WindowHeight { get; set; } = 900;

    /// <summary>
    /// Fraction (0–1) of the Audio tab split grid's total width allocated to the
    /// left (Channel Mapping) panel.  Persisted so the user's chosen split is
    /// remembered across restarts.  Defaults to 0.5 (equal halves).
    /// </summary>
    public double AudioSplitterRatio { get; set; } = 0.5;

    /// <summary>Whether the Output Devices card is expanded. Defaults to false (collapsed).</summary>
    public bool OutputDevicesExpanded { get; set; } = false;

    public ChannelSettings[] Channels { get; set; } = CreateDefaultChannels();

    public string[] ChannelTargetKeys { get; set; } = Array.Empty<string>();

    // Named profiles (v2.25+). Each profile stores its own set of 6 channel settings.
    // The active profile's Channels array is always mirrored into the Channels property
    // above so older code paths continue to work unchanged.
    public List<ProfileEntry> Profiles { get; set; } = new();
    public string ActiveProfileName { get; set; } = string.Empty;

    // Global system hotkeys (v2.29+).
    public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

    // On-screen volume overlay (v2.34+).
    public bool OverlayEnabled { get; set; } = true;
    public string OverlayPosition { get; set; } = "BottomCenter";
    public double OverlayTimeoutSeconds { get; set; } = 2.5;
    // Overlay appearance (v3.15+). Clamped at consumption in VolumeOverlay:
    // opacity 0.30–1.00 (1.0 = fully opaque), scale 0.75–1.50 (1.0 = default size).
    // AllScreens mirrors the popup on every monitor instead of the primary only.
    public double OverlayOpacity { get; set; } = 1.0;
    public double OverlayScale { get; set; } = 1.0;
    public bool OverlayAllScreens { get; set; } = false;

    // Output device cycle list (v2.35+). Device IDs included in the cycle, in order.
    public List<string> OutputDeviceCycleList { get; set; } = new();

    // Audio backend mode (v2.48+). "WASAPI" = Windows audio sessions (default).
    // "VoiceMeeter" = route volume through VoiceMeeter Remote API.
    public string AudioBackendMode { get; set; } = AudioBackendModes.Wasapi;

    // Software update preferences (v3.18+). Introduced here so the wizard's auto-update
    // page and the Quick-defaults table can bind them; the v3.19 updater engine only
    // *reads* these (it must not re-declare them). AutoCheckForUpdates defaults ON (a
    // background check on launch is low-cost and users expect it); AutoApplyUpdates
    // defaults OFF (downloading + launching an installer is a deliberate, opt-in action).
    // Both are independently disableable. Fresh-install and existing users share these
    // defaults (a missing JSON field deserialises to the initializer value).
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoApplyUpdates { get; set; } = false;

    // Auto-updater engine bookkeeping (v3.19). Not user-facing toggles — the updater
    // writes these. LastUpdateCheckUtc stamps the last successful check so the launch
    // check is throttled (rapid restarts don't re-query GitHub); a default (MinValue)
    // means "never checked" and always checks. SkippedUpdateVersion remembers a release
    // the user dismissed with "Skip this version" so it stops prompting until a strictly
    // newer release appears (whose version no longer matches). See Core UpdatePolicy.
    public System.DateTime LastUpdateCheckUtc { get; set; }
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    public static DashboardSettings CreateDefault()
    {
        return new DashboardSettings
        {
            SettingsVersion = 1,
            AutoConnectOnLaunch = true,
            FirstRunWizardCompleted = true,
            ScanAllComPortsIfRememberedMissing = true,
            Channels = CreateDefaultChannels(),
            ChannelTargetKeys = CreateDefaultChannels().Select(channel => channel.TargetKey).ToArray()
        };
    }

    /// <summary>
    /// Suggested friendly-name placeholders for each channel (1-based order), surfaced by
    /// the wizard / assign UI as hints only — they are NOT written as bindings. A fresh
    /// install binds only ch1 = Master; ch2–ch6 start unassigned (see CreateDefaultChannels).
    /// </summary>
    public static readonly string[] SuggestedChannelNames =
    {
        "Master", "Browser", "Music", "Voice Chat", "Game", "Microphone"
    };

    public static ChannelSettings[] CreateDefaultChannels()
    {
        // Fresh-install default (v3.16): only ch1 = Master is actually bound. ch2–ch6 start
        // Unassigned (empty TargetKey/FriendlyName); the SuggestedChannelNames above are shown
        // by the wizard/assign UI as placeholders. No channel starts in multi-app pool mode.
        return new[]
        {
            new ChannelSettings { TargetKey = "MASTER", FriendlyName = "Master", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "", FriendlyName = "", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "", FriendlyName = "", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "", FriendlyName = "", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "", FriendlyName = "", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "", FriendlyName = "", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction }
        };
    }
}

public sealed class ChannelSettings
{
    public string TargetKey { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ButtonAction { get; set; } = ChannelButtonActions.ToggleAssignedMute;
    public string LongPressButtonAction { get; set; } = ChannelButtonActions.NoAction;
    public string DoublePressButtonAction { get; set; } = ChannelButtonActions.NoAction;

    // What to do when the assigned app is not running.
    // ShowInactive: grey out channel row + OLED shows "App offline"
    // DoNothing:    channel stays silently inactive (previous behaviour)
    public string RebindFallback { get; set; } = RebindFallbacks.ShowInactive;

    // Per-channel OLED display mode.
    // Empty string = inherit the global mode from OLED Setup.
    // Non-empty = override with this specific mode for this channel.
    public string OledDisplayMode { get; set; } = string.Empty;

    // Per-channel encoder sensitivity override.
    // -1 = inherit the global EncoderSensitivityPercent.
    // 0–500 = use this value for this channel only.
    public int SensitivityPercent { get; set; } = -1;

    // Per-channel volume limits (percent, inclusive).
    // The encoder and smoothing paths clamp every write to this range.
    // Defaults give full 0–100 % range (i.e. unconstrained).
    public int MinVolumePercent { get; set; } = 0;
    public int MaxVolumePercent { get; set; } = 100;

    // Per-channel mute toggle hotkey (defaults to unassigned).
    public HotkeyBinding MuteHotkey { get; set; } = new HotkeyBinding();

    // Per-channel volume presets. Index 0 = Preset 1, 1 = Preset 2, 2 = Preset 3.
    public VolumePreset[] Presets { get; set; } = new[]
    {
        new VolumePreset { Name = "", VolumePercent = 25 },
        new VolumePreset { Name = "", VolumePercent = 50 },
        new VolumePreset { Name = "", VolumePercent = 75 },
    };

    // Channel linking group identifier (v2.56+).
    // Channels that share the same non-empty string are "linked": when an encoder
    // moves one channel by a given delta, all other channels in the group receive the
    // same delta (ganged-potentiometer behaviour).
    // Empty string = not linked.
    public string LinkedGroupId { get; set; } = string.Empty;

    // Multi-app pool (v2.58+). Ordered list of PROC:/MASTER/MIC_INPUT keys to
    // watch. At runtime the first key with an active audio session wins.
    // When this list has exactly one entry it behaves identically to TargetKey.
    public List<string> TargetKeys { get; set; } = new();
}

public sealed class VolumePreset
{
    public string Name { get; set; } = "";
    public int VolumePercent { get; set; } = 50;
}
