// MainWindow.Hotkeys.cs — Global hotkey registration, WndProc hook, and hotkey event handling.
// Extracted from MainWindow.xaml.cs in v2.43. All fields and P/Invoke declarations remain
// in MainWindow.xaml.cs.

using System;
using System.Windows;
using System.Windows.Interop;

namespace PcVolumeControllerDashboard;

public partial class MainWindow
{

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);

        RegisterAllHotkeys();

        ApplyTheme();
    }


    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            int eventType = wParam.ToInt32();

            if (eventType == DbtDeviceArrival || eventType == DbtDeviceRemoveComplete || eventType == DbtDevNodesChanged)
            {
                QueueDebouncedDeviceChangeRefresh($"Windows device-change event 0x{eventType:X}");
            }
        }

        if (msg == WmHotKey)
        {
            HandleHotkeyEvent(wParam.ToInt32());
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }


    private void RegisterAllHotkeys()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        UnregisterAllHotkeys(hwnd);

        RegisterHotkeyIfAssigned(hwnd, HotkeyIdMasterVolumeUp,   _settings.Hotkeys.MasterVolumeUp);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdMasterVolumeDown, _settings.Hotkeys.MasterVolumeDown);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdToggleMasterMute, _settings.Hotkeys.ToggleMasterMute);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdCycleNextProfile, _settings.Hotkeys.CycleNextProfile);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdShowDashboard,    _settings.Hotkeys.ShowDashboard);

        // Per-channel mute hotkeys
        for (int i = 0; i < _settings.Channels.Length && i < ExpectedChannelCount; i++)
            RegisterHotkeyIfAssigned(hwnd, HotkeyIdChannelMuteBase + i, _settings.Channels[i].MuteHotkey);
    }


    private static void RegisterHotkeyIfAssigned(IntPtr hwnd, int id, HotkeyBinding binding)
    {
        if (!binding.IsAssigned) return;
        RegisterHotKey(hwnd, id, (uint)binding.Modifiers, (uint)binding.VirtualKey);
    }


    private static void UnregisterAllHotkeys(IntPtr hwnd)
    {
        foreach (int id in AllHotkeyIds)
            UnregisterHotKey(hwnd, id);
    }


    private void HandleHotkeyEvent(int id)
    {
        switch (id)
        {
            case HotkeyIdMasterVolumeUp:
                try
                {
                    _audioBackend?.AdjustVolumeByKey("MASTER", GetVolumeStepPercent(), 0, 100);
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey master vol up error: {ex.Message}"); }
                break;

            case HotkeyIdMasterVolumeDown:
                try
                {
                    _audioBackend?.AdjustVolumeByKey("MASTER", -GetVolumeStepPercent(), 0, 100);
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey master vol down error: {ex.Message}"); }
                break;

            case HotkeyIdToggleMasterMute:
                try
                {
                    _audioBackend?.ToggleMuteByKey("MASTER");
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey toggle mute error: {ex.Message}"); }
                break;

            case HotkeyIdCycleNextProfile:
                CycleToNextProfile();
                break;

            case HotkeyIdShowDashboard:
                RestoreFromTray();
                break;

            default:
                // Per-channel mute hotkeys (IDs: HotkeyIdChannelMuteBase + channel index)
                int channelIndex = id - HotkeyIdChannelMuteBase;
                if (channelIndex >= 0 && channelIndex < ExpectedChannelCount && channelIndex < _channels.Count)
                {
                    try
                    {
                        ToggleChannelMute(channelIndex);
                        RefreshAllChannelStates();
                        SendAllChannelStatesToDevice();
                        SendStateToDevice(force: true);
                    }
                    catch (Exception ex) { Log($"Hotkey channel {channelIndex + 1} mute error: {ex.Message}"); }
                }
                break;
        }
    }


    // ── Global Hotkey UI handlers ────────────────────────────────────────────────

    private void UpdateHotkeyLabels()
    {
        if (HotkeyMasterVolUpTextBlock    != null) HotkeyMasterVolUpTextBlock.Text    = _settings.Hotkeys.MasterVolumeUp.ToDisplayString();
        if (HotkeyMasterVolDownTextBlock  != null) HotkeyMasterVolDownTextBlock.Text  = _settings.Hotkeys.MasterVolumeDown.ToDisplayString();
        if (HotkeyToggleMasterMuteTextBlock != null) HotkeyToggleMasterMuteTextBlock.Text = _settings.Hotkeys.ToggleMasterMute.ToDisplayString();
        if (HotkeyCycleNextProfileTextBlock != null) HotkeyCycleNextProfileTextBlock.Text = _settings.Hotkeys.CycleNextProfile.ToDisplayString();
        if (HotkeyShowDashboardTextBlock  != null) HotkeyShowDashboardTextBlock.Text  = _settings.Hotkeys.ShowDashboard.ToDisplayString();
    }


    private void SetHotkeyBinding(string actionName, HotkeyBinding current, Action<HotkeyBinding> apply)
    {
        var dialog = new HotkeyPickerDialog(actionName, current) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            apply(dialog.Result);
            FlushUiToSettings();
            UpdateHotkeyLabels();
            RegisterAllHotkeys();
        }
    }

}
