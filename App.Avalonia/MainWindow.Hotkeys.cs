using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Setup-tab "Global Hotkeys" handlers: shows the current binding for each global
/// action, opens the capture dialog to (re)assign one, clears one, and re-syncs the
/// <see cref="GlobalHotkeyManager"/> so changes take effect immediately. Mirrors the
/// WPF host's SetHotkeyBinding / UpdateHotkeyLabels (CycleNextProfile descoped).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Refreshes the four hotkey labels from the current settings.</summary>
    private void UpdateHotkeyLabels()
    {
        HotkeySettings h = _settings.Hotkeys;
        HotkeyMasterUpText.Text      = HotkeyDisplay.Describe(h.MasterVolumeUp);
        HotkeyMasterDownText.Text    = HotkeyDisplay.Describe(h.MasterVolumeDown);
        HotkeyMasterMuteText.Text    = HotkeyDisplay.Describe(h.ToggleMasterMute);
        HotkeyShowDashboardText.Text = HotkeyDisplay.Describe(h.ShowDashboard);
    }

    private async void SetHotkey(string actionName, HotkeyBinding current, Action<HotkeyBinding> apply)
    {
        HotkeyBinding? result = await HotkeyPickerDialog.ShowAsync(this, actionName, current);
        if (result == null) return; // cancelled

        apply(result);
        Save();
        UpdateHotkeyLabels();
        App.Services.GetService<GlobalHotkeyManager>()?.SyncFromSettings();
    }

    private void ClearHotkey(Action<HotkeyBinding> apply)
    {
        apply(new HotkeyBinding { Enabled = false });
        Save();
        UpdateHotkeyLabels();
        App.Services.GetService<GlobalHotkeyManager>()?.SyncFromSettings();
    }

    // ── Master volume up ──────────────────────────────────────────────────────
    private void HotkeyMasterUpSet_Click(object? sender, RoutedEventArgs e) =>
        SetHotkey("Master volume up", _settings.Hotkeys.MasterVolumeUp, b => _settings.Hotkeys.MasterVolumeUp = b);
    private void HotkeyMasterUpClear_Click(object? sender, RoutedEventArgs e) =>
        ClearHotkey(b => _settings.Hotkeys.MasterVolumeUp = b);

    // ── Master volume down ────────────────────────────────────────────────────
    private void HotkeyMasterDownSet_Click(object? sender, RoutedEventArgs e) =>
        SetHotkey("Master volume down", _settings.Hotkeys.MasterVolumeDown, b => _settings.Hotkeys.MasterVolumeDown = b);
    private void HotkeyMasterDownClear_Click(object? sender, RoutedEventArgs e) =>
        ClearHotkey(b => _settings.Hotkeys.MasterVolumeDown = b);

    // ── Toggle master mute ────────────────────────────────────────────────────
    private void HotkeyMasterMuteSet_Click(object? sender, RoutedEventArgs e) =>
        SetHotkey("Toggle master mute", _settings.Hotkeys.ToggleMasterMute, b => _settings.Hotkeys.ToggleMasterMute = b);
    private void HotkeyMasterMuteClear_Click(object? sender, RoutedEventArgs e) =>
        ClearHotkey(b => _settings.Hotkeys.ToggleMasterMute = b);

    // ── Show dashboard ────────────────────────────────────────────────────────
    private void HotkeyShowDashboardSet_Click(object? sender, RoutedEventArgs e) =>
        SetHotkey("Show dashboard", _settings.Hotkeys.ShowDashboard, b => _settings.Hotkeys.ShowDashboard = b);
    private void HotkeyShowDashboardClear_Click(object? sender, RoutedEventArgs e) =>
        ClearHotkey(b => _settings.Hotkeys.ShowDashboard = b);
}
