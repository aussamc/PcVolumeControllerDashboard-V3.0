using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Owns all settings I/O: path resolution, load, save, backup, and migration.
/// Extracted from MainWindow in v2.40 to decouple persistence from the UI layer.
/// </summary>
public static class SettingsRepository
{
    // ── Path helpers ─────────────────────────────────────────────────────────────

    public static string GetPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PcVolumeController", "settings.json");
    }

    public static string GetBackupDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PcVolumeController", "setup_backups");
    }

    // ── Load result ──────────────────────────────────────────────────────────────

    public sealed class LoadResult
    {
        /// <summary>The settings object (defaults if file was missing or corrupt).</summary>
        public DashboardSettings Settings { get; init; } = new();

        /// <summary>True if the file existed but could not be parsed.</summary>
        public bool WasCorrupt { get; init; }

        /// <summary>Path to the most recent backup file, if one was found.</summary>
        public string? LatestBackupPath { get; init; }

        /// <summary>True if a schema migration was applied and the file should be re-saved.</summary>
        public bool MigrationApplied { get; init; }
    }

    // ── Load ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads settings from disk.  Never throws — all errors are represented
    /// in the returned <see cref="LoadResult"/>.
    /// </summary>
    public static LoadResult Load(int channelCount, int maxEncoderSensitivityPercent)
    {
        string path = GetPath();

        if (!File.Exists(path))
        {
            return new LoadResult { Settings = DashboardSettings.CreateDefault() };
        }

        // Attempt to parse.
        DashboardSettings? parsed = null;
        try
        {
            string json = File.ReadAllText(path);
            parsed = JsonSerializer.Deserialize<DashboardSettings>(json);
        }
        catch
        {
            // parsed stays null
        }

        if (parsed == null)
        {
            // File exists but is corrupt — find most recent backup to offer to the user.
            string? latestBackup = null;
            string backupDir = GetBackupDirectory();
            if (Directory.Exists(backupDir))
            {
                latestBackup = Directory.GetFiles(backupDir, "settings-*.json")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }

            return new LoadResult
            {
                Settings = DashboardSettings.CreateDefault(),
                WasCorrupt = true,
                LatestBackupPath = latestBackup
            };
        }

        bool migrated = Normalize(parsed, channelCount, maxEncoderSensitivityPercent);
        return new LoadResult
        {
            Settings = parsed,
            MigrationApplied = migrated
        };
    }

    // ── Save ──────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Serialises <paramref name="settings"/> to the standard settings path.
    /// Errors are reported via <paramref name="logger"/> (never thrown).
    /// </summary>
    public static void Save(DashboardSettings settings, Action<string>? logger = null)
    {
        try
        {
            string path = GetPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(settings, _writeOptions);

            // Write atomically: serialise to a temporary file first, then move it into
            // place.  If the process is terminated mid-write (e.g. during a Windows
            // shutdown/restart), the live settings.json is never left half-written and
            // therefore never read back as corrupt on the next launch.
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"Settings save error: {ex.Message}");
        }
    }

    // ── Import / export (arbitrary paths) ─────────────────────────────────────────

    /// <summary>
    /// Serialises <paramref name="settings"/> to an arbitrary file (user "export
    /// settings"). Creates the parent directory; propagates I/O errors to the caller.
    /// </summary>
    public static void ExportTo(DashboardSettings settings, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, _writeOptions));
    }

    /// <summary>
    /// Reads and normalises settings from an arbitrary file (user "import settings").
    /// Returns null if the file is missing or cannot be parsed — never throws.
    /// </summary>
    public static DashboardSettings? ImportFrom(string path, int channelCount, int maxEncoderSensitivityPercent)
    {
        if (!File.Exists(path))
            return null;

        DashboardSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DashboardSettings>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }

        if (parsed == null)
            return null;

        Normalize(parsed, channelCount, maxEncoderSensitivityPercent);
        return parsed;
    }

    // ── Backup ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the current settings file to the backup directory tagged with
    /// <paramref name="reason"/> and a timestamp. Keeps only the 10 most
    /// recent backups; older files are pruned silently.
    /// </summary>
    public static void Backup(string reason, Action<string>? logger = null)
    {
        try
        {
            string settingsPath = GetPath();
            if (!File.Exists(settingsPath))
                return;

            string backupDir = GetBackupDirectory();
            Directory.CreateDirectory(backupDir);
            string backupPath = Path.Combine(backupDir, $"settings-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(settingsPath, backupPath, overwrite: false);
            logger?.Invoke($"Backed up current setup to: {backupPath}");

            // Prune old backups — keep 10 most recent.
            try
            {
                string[] stale = Directory.GetFiles(backupDir, "settings-*.json")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Skip(10)
                    .ToArray();
                foreach (string old in stale)
                    File.Delete(old);
            }
            catch
            {
                // Non-critical — ignore prune failures.
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"Setup backup failed: {ex.Message}");
        }
    }

    // ── Normalise / migrate ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies schema migrations and clamps out-of-range values in-place.
    /// Returns <c>true</c> if any migration was applied (caller should persist).
    /// </summary>
    public static bool Normalize(DashboardSettings settings, int channelCount, int maxEncoderSensitivityPercent)
    {
        bool migrated = false;

        // v0 → v1: auto-connect, first-run wizard, and scan-all were accidentally
        // defaulted to false. Correct them on first run of any build that includes
        // this migration so existing users get the right out-of-box behaviour.
        if (settings.SettingsVersion < 1)
        {
            settings.AutoConnectOnLaunch = true;
            settings.FirstRunWizardCompleted = true;
            settings.ScanAllComPortsIfRememberedMissing = true;
            settings.SettingsVersion = 1;
            migrated = true;
        }

        if (settings.Channels == null || settings.Channels.Length != channelCount)
            settings.Channels = DashboardSettings.CreateDefaultChannels();

        settings.ChannelTargetKeys ??= settings.Channels.Select(ch => ch.TargetKey).ToArray();

        if (string.IsNullOrWhiteSpace(settings.ThemeMode))
            settings.ThemeMode = ThemeModes.FollowSystem;

        // v1 → v2: 6-encoder hardware is now installed. The old SelectNextChannel action
        // was a prototype workaround (1 encoder cycling through channels). Migrate every
        // channel that still has that action to NoAction so each encoder controls its own
        // dedicated channel's volume independently.
        if (settings.SettingsVersion < 2)
        {
            foreach (ChannelSettings channel in settings.Channels)
            {
                if (channel.ButtonAction == ChannelButtonActions.SelectNextChannel)
                    channel.ButtonAction = ChannelButtonActions.NoAction;
            }
            settings.SettingsVersion = 2;
            migrated = true;
        }

        // v2 → v3: Short press default changed from ToggleAssignedMute to NoAction.
        // A dedicated long-press already handles mute; accidental short clicks while
        // turning the encoder should not trigger mute.
        if (settings.SettingsVersion < 3)
        {
            foreach (ChannelSettings channel in settings.Channels)
            {
                if (channel.ButtonAction == ChannelButtonActions.ToggleAssignedMute)
                    channel.ButtonAction = ChannelButtonActions.NoAction;
            }
            settings.SettingsVersion = 3;
            migrated = true;
        }

        settings.EncoderSensitivityPercent = Math.Clamp(settings.EncoderSensitivityPercent, 0, maxEncoderSensitivityPercent);

        foreach (ChannelSettings channel in settings.Channels)
        {
            if (!ChannelButtonActions.IsValid(channel.ButtonAction))
                channel.ButtonAction = ChannelButtonActions.NoAction;

            if (!ChannelButtonActions.IsValidLongPressAction(channel.LongPressButtonAction))
                channel.LongPressButtonAction = ChannelButtonActions.NoAction;

            if (!ChannelButtonActions.IsValidDoublePressAction(channel.DoublePressButtonAction))
                channel.DoublePressButtonAction = ChannelButtonActions.NoAction;

            if (!RebindFallbacks.IsValid(channel.RebindFallback))
                channel.RebindFallback = RebindFallbacks.ShowInactive;

            if (!DisplayModes.IsValidChannelMode(channel.OledDisplayMode))
                channel.OledDisplayMode = string.Empty;
        }

        // v3 → v4: Short press default restored to ToggleAssignedMute.  The v2→v3 migration
        // moved everyone to NoAction when a dedicated long-press mute was introduced; that
        // was overly conservative.  Channels still on NoAction are migrated back so new and
        // existing users both get mute-on-short-press out of the box.
        if (settings.SettingsVersion < 4)
        {
            foreach (ChannelSettings channel in settings.Channels)
            {
                if (channel.ButtonAction == ChannelButtonActions.NoAction)
                    channel.ButtonAction = ChannelButtonActions.ToggleAssignedMute;
            }
            settings.SettingsVersion = 4;
            migrated = true;
        }

        if (!AccelerationPresets.IsValid(settings.AccelerationPreset))
            settings.AccelerationPreset = AccelerationPresets.Medium;

        settings.AccelThresholdMs   = Math.Clamp(settings.AccelThresholdMs,   20,  250);
        settings.AccelMaxMultiplier = Math.Clamp(settings.AccelMaxMultiplier, 1.5f, 8.0f);
        settings.AccelCurveExponent = Math.Clamp(settings.AccelCurveExponent, 0.3f, 2.5f);

        if (!SmoothingSpeed.IsValid(settings.VolumeSmoothingSpeed))
            settings.VolumeSmoothingSpeed = SmoothingSpeed.Normal;

        settings.OledBrightnessPercent = Math.Clamp(
            settings.OledBrightnessPercent <= 0 ? 100 : settings.OledBrightnessPercent, 0, 100);
        settings.OledSleepTimeoutMinutes = Math.Clamp(
            settings.OledSleepTimeoutMinutes <= 0 ? 2 : settings.OledSleepTimeoutMinutes, 1, 60);
        settings.OledConnectedIdleTimeoutMinutes = Math.Clamp(
            settings.OledConnectedIdleTimeoutMinutes <= 0 ? 10 : settings.OledConnectedIdleTimeoutMinutes, 1, 60);

        if (string.IsNullOrWhiteSpace(settings.OledConnectedIdleAction) ||
            !OledIdleActions.IsValid(settings.OledConnectedIdleAction))
            settings.OledConnectedIdleAction = OledIdleActions.DimTo30;

        if (string.IsNullOrWhiteSpace(settings.OledDisplayMode))
            settings.OledDisplayMode = DisplayModes.AppNameAndVolume;

        settings.SelectedChannelIndex = Math.Clamp(settings.SelectedChannelIndex, 0, channelCount - 1);

        // v4 → v5: Named profiles introduced. Create a single "Default" profile from
        // the current channel settings so existing setups are migrated automatically.
        if (settings.SettingsVersion < 5)
        {
            settings.Profiles ??= new List<ProfileEntry>();
            if (settings.Profiles.Count == 0)
            {
                settings.Profiles.Add(new ProfileEntry
                {
                    Name = "Default",
                    Channels = settings.Channels.Select(ch => new ChannelSettings
                    {
                        TargetKey               = ch.TargetKey,
                        FriendlyName            = ch.FriendlyName,
                        ButtonAction            = ch.ButtonAction,
                        LongPressButtonAction   = ch.LongPressButtonAction,
                        DoublePressButtonAction = ch.DoublePressButtonAction,
                        RebindFallback          = ch.RebindFallback,
                        OledDisplayMode         = ch.OledDisplayMode,
                        SensitivityPercent      = ch.SensitivityPercent,
                        MinVolumePercent        = ch.MinVolumePercent,
                        MaxVolumePercent        = ch.MaxVolumePercent,
                        MuteHotkey              = new HotkeyBinding
                        {
                            Enabled    = ch.MuteHotkey.Enabled,
                            Modifiers  = ch.MuteHotkey.Modifiers,
                            VirtualKey = ch.MuteHotkey.VirtualKey,
                        },
                        Presets                 = ch.Presets?.Select(p => new VolumePreset
                        {
                            Name          = p.Name,
                            VolumePercent = p.VolumePercent,
                        }).ToArray() ?? Array.Empty<VolumePreset>(),
                        LinkedGroupId           = ch.LinkedGroupId,
                    }).ToArray()
                });
                settings.ActiveProfileName = "Default";
            }
            settings.SettingsVersion = 5;
            migrated = true;
        }

        // Validate profiles: null-guard, fix bad channel arrays, ensure at least one profile,
        // ensure the active name points to an existing profile.
        settings.Profiles ??= new List<ProfileEntry>();
        foreach (ProfileEntry profile in settings.Profiles)
        {
            if (profile.Channels == null || profile.Channels.Length != channelCount)
                profile.Channels = DashboardSettings.CreateDefaultChannels();
        }
        if (settings.Profiles.Count == 0)
        {
            settings.Profiles.Add(new ProfileEntry
            {
                Name = "Default",
                Channels = DashboardSettings.CreateDefaultChannels()
            });
        }
        if (settings.Profiles.All(p => p.Name != settings.ActiveProfileName))
            settings.ActiveProfileName = settings.Profiles[0].Name;

        // Sync Channels from the active profile so ApplySettingsToChannels reads
        // the profile data, not a stale copy from an older settings field.
        ProfileEntry? activeProfile = settings.Profiles.FirstOrDefault(p => p.Name == settings.ActiveProfileName);
        if (activeProfile != null)
            settings.Channels = activeProfile.Channels;

        // v5 → v6: AudioBackendMode introduced (default "WASAPI").
        // Validate the stored value and default to WASAPI if unknown.
        if (settings.SettingsVersion < 6)
        {
            if (!AudioBackendModes.IsValid(settings.AudioBackendMode))
                settings.AudioBackendMode = AudioBackendModes.Wasapi;
            settings.SettingsVersion = 6;
            migrated = true;
        }

        // Ongoing: always validate AudioBackendMode in case of manual edits.
        if (!AudioBackendModes.IsValid(settings.AudioBackendMode))
            settings.AudioBackendMode = AudioBackendModes.Wasapi;

        // v6 → v7: Per-channel volume limits (MinVolumePercent / MaxVolumePercent) introduced.
        // Clamp existing values into valid range; fix inverted pairs.
        if (settings.SettingsVersion < 7)
        {
            foreach (ChannelSettings ch in settings.Channels)
            {
                ch.MinVolumePercent = Math.Clamp(ch.MinVolumePercent, 0, 100);
                ch.MaxVolumePercent = Math.Clamp(ch.MaxVolumePercent, 0, 100);
                if (ch.MinVolumePercent > ch.MaxVolumePercent)
                    (ch.MinVolumePercent, ch.MaxVolumePercent) = (ch.MaxVolumePercent, ch.MinVolumePercent);
            }
            settings.SettingsVersion = 7;
            migrated = true;
        }

        // Ongoing: always validate volume limits in case of manual edits.
        foreach (ChannelSettings ch in settings.Channels)
        {
            ch.MinVolumePercent = Math.Clamp(ch.MinVolumePercent, 0, 100);
            ch.MaxVolumePercent = Math.Clamp(ch.MaxVolumePercent, 0, 100);
            if (ch.MinVolumePercent > ch.MaxVolumePercent)
                ch.MaxVolumePercent = ch.MinVolumePercent;
        }

        // v7 → v8: TargetKeys (multi-app pool) introduced.
        // Seed TargetKeys from the existing single TargetKey so existing
        // assignments are preserved without the user having to re-configure.
        if (settings.SettingsVersion < 8)
        {
            foreach (ChannelSettings ch in settings.Channels)
            {
                ch.TargetKeys ??= new List<string>();
                if (ch.TargetKeys.Count == 0 && !string.IsNullOrWhiteSpace(ch.TargetKey))
                    ch.TargetKeys.Add(ch.TargetKey);
            }
            foreach (ProfileEntry profile in settings.Profiles ?? new List<ProfileEntry>())
            {
                foreach (ChannelSettings ch in profile.Channels ?? Array.Empty<ChannelSettings>())
                {
                    ch.TargetKeys ??= new List<string>();
                    if (ch.TargetKeys.Count == 0 && !string.IsNullOrWhiteSpace(ch.TargetKey))
                        ch.TargetKeys.Add(ch.TargetKey);
                }
            }
            settings.SettingsVersion = 8;
            migrated = true;
        }

        // Ongoing: ensure TargetKeys is never null and is consistent with TargetKey.
        foreach (ChannelSettings ch in settings.Channels)
        {
            ch.TargetKeys ??= new List<string>();
            if (ch.TargetKeys.Count == 0 && !string.IsNullOrWhiteSpace(ch.TargetKey))
                ch.TargetKeys.Add(ch.TargetKey);
        }

        return migrated;
    }
}
