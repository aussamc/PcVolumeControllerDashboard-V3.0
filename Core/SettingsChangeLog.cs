using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PcVolumeControllerDashboard.Core;

/// <summary>Severity a settings change should be logged at.</summary>
public enum SettingsChangeLevel
{
    /// <summary>Discrete, user-meaningful config change (assignment, toggle, mode).</summary>
    Info,

    /// <summary>Continuous / drag-style input (a slider value) — only surfaced under advanced logging.</summary>
    Debug,
}

/// <summary>A single detected settings change, ready to be written to the log.</summary>
public readonly record struct SettingsChange(SettingsChangeLevel Level, string Description);

/// <summary>
/// Pure diff of two <see cref="DashboardSettings"/> snapshots into human-readable change
/// lines for the audit log. Discrete config changes (channel assignments, toggles, modes,
/// hotkeys) are tagged <see cref="SettingsChangeLevel.Info"/>; continuous/drag-style inputs
/// (sliders — sensitivity, brightness, overlay opacity/scale, custom-curve values, timeouts)
/// are tagged <see cref="SettingsChangeLevel.Debug"/> so they only appear under advanced
/// logging. Incidental/bookkeeping state (window geometry, splitter ratio, last COM port,
/// paired chip id, selected-channel index, first-run flag, update-check bookkeeping, the
/// Profiles list and the ChannelTargetKeys mirror) is deliberately ignored.
///
/// Host-free so the classification is unit-tested; the host clones settings around a save
/// and writes each returned change at its <see cref="SettingsChange.Level"/>.
/// </summary>
public static class SettingsChangeLog
{
    public static IReadOnlyList<SettingsChange> Diff(DashboardSettings? before, DashboardSettings? after)
    {
        var changes = new List<SettingsChange>();
        if (before == null || after == null)
            return changes;

        // Connection / app-behaviour toggles.
        Info(changes, "AutoConnectOnLaunch", before.AutoConnectOnLaunch, after.AutoConnectOnLaunch);
        Info(changes, "ScanAllComPortsIfRememberedMissing", before.ScanAllComPortsIfRememberedMissing, after.ScanAllComPortsIfRememberedMissing);
        Info(changes, "MinimizeToTray", before.MinimizeToTray, after.MinimizeToTray);
        Info(changes, "StartMinimizedToTray", before.StartMinimizedToTray, after.StartMinimizedToTray);
        Info(changes, "StartWithWindows", before.StartWithWindows, after.StartWithWindows);
        Info(changes, "AdvancedDebugLogging", before.AdvancedDebugLogging, after.AdvancedDebugLogging);
        Info(changes, "TrayNotificationsEnabled", before.TrayNotificationsEnabled, after.TrayNotificationsEnabled);
        Info(changes, "AdvancedDebugFeatures", before.AdvancedDebugFeatures, after.AdvancedDebugFeatures);

        // Encoder feel.
        Debug(changes, "EncoderSensitivityPercent", before.EncoderSensitivityPercent, after.EncoderSensitivityPercent);
        Info(changes, "AccelerationEnabled", before.AccelerationEnabled, after.AccelerationEnabled);
        Info(changes, "AccelerationPreset", before.AccelerationPreset, after.AccelerationPreset);
        Info(changes, "VolumeSmoothingEnabled", before.VolumeSmoothingEnabled, after.VolumeSmoothingEnabled);
        Info(changes, "VolumeSmoothingSpeed", before.VolumeSmoothingSpeed, after.VolumeSmoothingSpeed);
        Debug(changes, "AccelThresholdMs", before.AccelThresholdMs, after.AccelThresholdMs);
        Debug(changes, "AccelMaxMultiplier", before.AccelMaxMultiplier, after.AccelMaxMultiplier);
        Debug(changes, "AccelCurveExponent", before.AccelCurveExponent, after.AccelCurveExponent);

        // Appearance / OLED.
        Info(changes, "ThemeMode", before.ThemeMode, after.ThemeMode);
        Info(changes, "OledDisplayMode", before.OledDisplayMode, after.OledDisplayMode);
        Debug(changes, "OledBrightnessPercent", before.OledBrightnessPercent, after.OledBrightnessPercent);
        Debug(changes, "OledSleepTimeoutMinutes", before.OledSleepTimeoutMinutes, after.OledSleepTimeoutMinutes);
        Info(changes, "OledConnectedIdleAction", before.OledConnectedIdleAction, after.OledConnectedIdleAction);
        Debug(changes, "OledConnectedIdleTimeoutMinutes", before.OledConnectedIdleTimeoutMinutes, after.OledConnectedIdleTimeoutMinutes);
        Info(changes, "OledAntiBurnInEnabled", before.OledAntiBurnInEnabled, after.OledAntiBurnInEnabled);

        // Volume overlay.
        Info(changes, "OverlayEnabled", before.OverlayEnabled, after.OverlayEnabled);
        Info(changes, "OverlayPosition", before.OverlayPosition, after.OverlayPosition);
        Debug(changes, "OverlayTimeoutSeconds", before.OverlayTimeoutSeconds, after.OverlayTimeoutSeconds);
        Debug(changes, "OverlayOpacity", before.OverlayOpacity, after.OverlayOpacity);
        Debug(changes, "OverlayScale", before.OverlayScale, after.OverlayScale);
        Info(changes, "OverlayAllScreens", before.OverlayAllScreens, after.OverlayAllScreens);

        // Audio backend, updates, misc UI toggles.
        Info(changes, "AudioBackendMode", before.AudioBackendMode, after.AudioBackendMode);
        Info(changes, "AutoCheckForUpdates", before.AutoCheckForUpdates, after.AutoCheckForUpdates);
        Info(changes, "AutoApplyUpdates", before.AutoApplyUpdates, after.AutoApplyUpdates);
        Info(changes, "OutputDevicesExpanded", before.OutputDevicesExpanded, after.OutputDevicesExpanded);
        Info(changes, "OutputDeviceCycleList", Join(before.OutputDeviceCycleList), Join(after.OutputDeviceCycleList));

        // Global hotkeys.
        DiffHotkeys(changes, before.Hotkeys, after.Hotkeys);

        // Per-channel settings.
        DiffChannels(changes, before.Channels, after.Channels);

        return changes;
    }

    private static void DiffHotkeys(List<SettingsChange> changes, HotkeySettings? before, HotkeySettings? after)
    {
        if (before == null || after == null)
            return;
        Info(changes, "Hotkey.MasterVolumeUp", Fmt(before.MasterVolumeUp), Fmt(after.MasterVolumeUp));
        Info(changes, "Hotkey.MasterVolumeDown", Fmt(before.MasterVolumeDown), Fmt(after.MasterVolumeDown));
        Info(changes, "Hotkey.ToggleMasterMute", Fmt(before.ToggleMasterMute), Fmt(after.ToggleMasterMute));
        Info(changes, "Hotkey.ShowDashboard", Fmt(before.ShowDashboard), Fmt(after.ShowDashboard));
    }

    private static void DiffChannels(List<SettingsChange> changes, ChannelSettings[]? before, ChannelSettings[]? after)
    {
        if (before == null || after == null)
            return;

        int count = Math.Min(before.Length, after.Length);
        for (int i = 0; i < count; i++)
        {
            ChannelSettings b = before[i];
            ChannelSettings a = after[i];
            string p = $"Ch{i + 1}.";

            Info(changes, p + "TargetKey", b.TargetKey, a.TargetKey);
            Info(changes, p + "TargetKeys", Join(b.TargetKeys), Join(a.TargetKeys));
            Info(changes, p + "FriendlyName", b.FriendlyName, a.FriendlyName);
            Info(changes, p + "ButtonAction", b.ButtonAction, a.ButtonAction);
            Info(changes, p + "LongPressButtonAction", b.LongPressButtonAction, a.LongPressButtonAction);
            Info(changes, p + "DoublePressButtonAction", b.DoublePressButtonAction, a.DoublePressButtonAction);
            Info(changes, p + "RebindFallback", b.RebindFallback, a.RebindFallback);
            Info(changes, p + "OledDisplayMode", b.OledDisplayMode, a.OledDisplayMode);
            Info(changes, p + "LinkedGroupId", b.LinkedGroupId, a.LinkedGroupId);
            Info(changes, p + "MuteHotkey", Fmt(b.MuteHotkey), Fmt(a.MuteHotkey));
            Info(changes, p + "Presets", Fmt(b.Presets), Fmt(a.Presets));

            Debug(changes, p + "SensitivityPercent", b.SensitivityPercent, a.SensitivityPercent);
            Debug(changes, p + "MinVolumePercent", b.MinVolumePercent, a.MinVolumePercent);
            Debug(changes, p + "MaxVolumePercent", b.MaxVolumePercent, a.MaxVolumePercent);
        }
    }

    private static void Info(List<SettingsChange> changes, string name, object? oldVal, object? newVal) =>
        Add(changes, SettingsChangeLevel.Info, name, oldVal, newVal);

    private static void Debug(List<SettingsChange> changes, string name, object? oldVal, object? newVal) =>
        Add(changes, SettingsChangeLevel.Debug, name, oldVal, newVal);

    private static void Add(List<SettingsChange> changes, SettingsChangeLevel level, string name, object? oldVal, object? newVal)
    {
        if (Equals(oldVal, newVal))
            return;
        changes.Add(new SettingsChange(level, $"{name}: {Fmt(oldVal)} -> {Fmt(newVal)}"));
    }

    private static string Join(IEnumerable<string>? items) =>
        items == null ? "(empty)" : (items.Any() ? string.Join("+", items) : "(empty)");

    private static string Fmt(object? value) => value switch
    {
        null => "(none)",
        string s => s.Length == 0 ? "(empty)" : s,
        bool b => b ? "on" : "off",
        HotkeyBinding h => h.IsAssigned ? $"{h.Modifiers}+{h.VirtualKey}" : "unassigned",
        VolumePreset[] presets => string.Join(", ", presets.Select(pr => $"{(pr.Name.Length == 0 ? "-" : pr.Name)}:{pr.VolumePercent}%")),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
