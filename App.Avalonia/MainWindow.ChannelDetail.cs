using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Audio tab — per-channel detail panel. Edits the settings the encoder/button
/// runtime (ChannelRuntime) actually consumes for the channel selected in the
/// grid: display name, short/long/double button actions, per-channel sensitivity
/// override, volume limits, the three volume presets, and the per-channel OLED
/// display mode. Writes flow back to <see cref="ChannelSettings"/> and are saved
/// immediately; OLED-mode changes are pushed to the device live.
///
/// A <see cref="_detailLoading"/> guard mirrors the Setup tab's _initializing flag
/// so populating the controls from settings never re-triggers a write-back.
/// </summary>
public partial class MainWindow : Window
{
    private bool _detailLoading;
    private int _detailIndex = -1;

    private sealed record Option(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    // Button actions offered for each press. The runtime implements mute / presets
    // / media now; select-next is accepted for parity and logged as not-yet-ported.
    // Named profiles and output-device cycling are descoped from the Avalonia port
    // (the latter kept in the roadmap), so they're intentionally not offered.
    private static readonly Option[] ButtonActionOptions =
    {
        new(ChannelButtonActions.ToggleAssignedMute, "Toggle mute"),
        new(ChannelButtonActions.NoAction,           "No action"),
        new(ChannelButtonActions.ApplyPreset1,       "Apply preset 1"),
        new(ChannelButtonActions.ApplyPreset2,       "Apply preset 2"),
        new(ChannelButtonActions.ApplyPreset3,       "Apply preset 3"),
        new(ChannelButtonActions.MediaPlayPause,     "Media: play / pause"),
        new(ChannelButtonActions.MediaNextTrack,     "Media: next track"),
        new(ChannelButtonActions.MediaPrevTrack,     "Media: previous track"),
        new(ChannelButtonActions.MediaStop,          "Media: stop"),
        new(ChannelButtonActions.SelectNextChannel,  "Select next channel (coming soon)"),
    };

    private static readonly Option[] OledModeOptions =
    {
        new(string.Empty,                  "Use global default"),
        new(DisplayModes.AppNameAndVolume, "App name + volume"),
        new(DisplayModes.LargeVolume,      "Large volume number"),
        new(DisplayModes.MuteStatus,       "Mute status"),
        new(DisplayModes.AppOrDeviceName,  "App / device name"),
        new(DisplayModes.BarPercent,       "Bar / percentage"),
    };

    private void InitChannelDetail()
    {
        DetailShortActionCombo.ItemsSource  = ButtonActionOptions;
        DetailLongActionCombo.ItemsSource   = ButtonActionOptions;
        DetailDoubleActionCombo.ItemsSource = ButtonActionOptions;
        DetailOledModeCombo.ItemsSource     = OledModeOptions;

        // Show the first channel's detail by default.
        if (_channelRows.Count > 0)
            ChannelGrid.SelectedIndex = 0;
        else
            LoadChannelDetail(-1);
    }

    private void ChannelGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => LoadChannelDetail(ChannelGrid.SelectedIndex);

    private void LoadChannelDetail(int index)
    {
        ChannelSettings[] channels = _settings.Channels;
        if (index < 0 || index >= channels.Length)
        {
            _detailIndex = -1;
            ChannelDetailPanel.IsEnabled = false;
            DetailHeaderText.Text = "Channel Detail";
            return;
        }

        _detailIndex = index;
        ChannelSettings ch = channels[index];
        EnsurePresets(ch);

        _detailLoading = true;
        ChannelDetailPanel.IsEnabled = true;
        DetailHeaderText.Text = $"Channel Detail — Channel {index + 1}";

        DetailNameBox.Text = ch.FriendlyName;
        SelectByKey(DetailShortActionCombo, ch.ButtonAction);
        SelectByKey(DetailLongActionCombo, ch.LongPressButtonAction);
        SelectByKey(DetailDoubleActionCombo, ch.DoublePressButtonAction);

        bool overrideSens = ch.SensitivityPercent >= 0;
        DetailSensOverrideCheck.IsChecked = overrideSens;
        DetailSensRow.IsEnabled = overrideSens;
        DetailSensSlider.Value = overrideSens
            ? Math.Clamp(ch.SensitivityPercent, 0, 500)
            : Math.Clamp(_settings.EncoderSensitivityPercent, 0, 500);
        UpdateDetailSensLabel();

        DetailMinVol.Value = Math.Clamp(ch.MinVolumePercent, 0, 100);
        DetailMaxVol.Value = Math.Clamp(ch.MaxVolumePercent, 0, 100);

        DetailPreset1Name.Text = ch.Presets[0].Name; DetailPreset1Vol.Value = ch.Presets[0].VolumePercent;
        DetailPreset2Name.Text = ch.Presets[1].Name; DetailPreset2Vol.Value = ch.Presets[1].VolumePercent;
        DetailPreset3Name.Text = ch.Presets[2].Name; DetailPreset3Vol.Value = ch.Presets[2].VolumePercent;

        SelectByKey(DetailOledModeCombo, ch.OledDisplayMode);

        _detailLoading = false;
    }

    /// <summary>The channel currently bound to the detail panel, or null while loading.</summary>
    private ChannelSettings? CurrentDetailChannel()
    {
        if (_detailLoading || _detailIndex < 0 || _detailIndex >= _settings.Channels.Length) return null;
        return _settings.Channels[_detailIndex];
    }

    // ── Display name ─────────────────────────────────────────────────────────

    private void DetailName_Committed(object? sender, RoutedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        ch.FriendlyName = (DetailNameBox.Text ?? string.Empty).Trim();
        Save();
        RefreshChannelStates();   // updates the grid row + pushes the CHSTATE label
        RenderOledPreviews();     // OLED tab previews use the saved names
    }

    private void DetailName_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DetailName_Committed(sender, e);
    }

    // ── Button actions ───────────────────────────────────────────────────────

    private void DetailShortAction_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        ch.ButtonAction = SelectedKey(DetailShortActionCombo, ChannelButtonActions.ToggleAssignedMute);
        Save();
    }

    private void DetailLongAction_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        ch.LongPressButtonAction = SelectedKey(DetailLongActionCombo, ChannelButtonActions.NoAction);
        Save();
    }

    private void DetailDoubleAction_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        ch.DoublePressButtonAction = SelectedKey(DetailDoubleActionCombo, ChannelButtonActions.NoAction);
        Save();
    }

    // ── Sensitivity ──────────────────────────────────────────────────────────

    private void DetailSensOverride_Changed(object? sender, RoutedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;

        bool on = DetailSensOverrideCheck.IsChecked == true;
        DetailSensRow.IsEnabled = on;
        if (on)
        {
            ch.SensitivityPercent = (int)Math.Round(DetailSensSlider.Value);
        }
        else
        {
            ch.SensitivityPercent = -1;
            _detailLoading = true;
            DetailSensSlider.Value = Math.Clamp(_settings.EncoderSensitivityPercent, 0, 500);
            _detailLoading = false;
            UpdateDetailSensLabel();
        }
        Save();
    }

    // Wired via the slider-observable in WireSliders (see MainWindow.axaml.cs).
    private void OnDetailSensChanged()
    {
        UpdateDetailSensLabel();
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        if (DetailSensOverrideCheck.IsChecked == true)
        {
            ch.SensitivityPercent = (int)Math.Round(DetailSensSlider.Value);
            Save();
        }
    }

    private void UpdateDetailSensLabel() =>
        DetailSensValueText.Text = $"{(int)Math.Round(DetailSensSlider.Value)}%";

    // ── Volume limits ────────────────────────────────────────────────────────

    private void DetailVolumeLimit_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;

        int min = (int)(DetailMinVol.Value ?? 0m);
        int max = (int)(DetailMaxVol.Value ?? 100m);
        ch.MinVolumePercent = Math.Clamp(Math.Min(min, max), 0, 100);
        ch.MaxVolumePercent = Math.Clamp(Math.Max(min, max), 0, 100);
        Save();
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    private void DetailPreset_Committed(object? sender, RoutedEventArgs e) => WriteDetailPresets();
    private void DetailPreset_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => WriteDetailPresets();

    private void WriteDetailPresets()
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        EnsurePresets(ch);

        ch.Presets[0].Name = DetailPreset1Name.Text ?? string.Empty;
        ch.Presets[0].VolumePercent = (int)(DetailPreset1Vol.Value ?? 0m);
        ch.Presets[1].Name = DetailPreset2Name.Text ?? string.Empty;
        ch.Presets[1].VolumePercent = (int)(DetailPreset2Vol.Value ?? 0m);
        ch.Presets[2].Name = DetailPreset3Name.Text ?? string.Empty;
        ch.Presets[2].VolumePercent = (int)(DetailPreset3Vol.Value ?? 0m);
        Save();
    }

    // ── Per-channel OLED mode ────────────────────────────────────────────────

    private void DetailOledMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ChannelSettings? ch = CurrentDetailChannel();
        if (ch == null) return;
        ch.OledDisplayMode = SelectedKey(DetailOledModeCombo, string.Empty);
        Save();
        _deviceState?.PushAllChannelOledModes(); // apply DISPMODE to the device live
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void SelectByKey(ComboBox combo, string key)
    {
        if (combo.ItemsSource is not System.Collections.IEnumerable items) return;
        foreach (object? item in items)
        {
            if (item is Option o && string.Equals(o.Key, key, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0; // unknown/blank → first option
    }

    private static string SelectedKey(ComboBox combo, string fallback) =>
        combo.SelectedItem is Option o ? o.Key : fallback;

    private static void EnsurePresets(ChannelSettings ch)
    {
        if (ch.Presets is { Length: >= 3 }) return;
        int[] defaults = { 25, 50, 75 };
        var fixedPresets = new VolumePreset[3];
        for (int i = 0; i < 3; i++)
            fixedPresets[i] = ch.Presets is not null && i < ch.Presets.Length
                ? ch.Presets[i]
                : new VolumePreset { Name = string.Empty, VolumePercent = defaults[i] };
        ch.Presets = fixedPresets;
    }
}
