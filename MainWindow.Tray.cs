// MainWindow.Tray.cs — System tray icon, tray menu, window show/hide/close lifecycle.
// Extracted from MainWindow.xaml.cs in v2.43. All fields remain in MainWindow.xaml.cs.

using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PcVolumeControllerDashboard;

public partial class MainWindow
{

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // Load icon from the embedded application resource (Assets/app-icon.ico).
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string icoPath = Path.Combine(exeDir, "Assets", "app-icon.ico");
            if (File.Exists(icoPath))
                return new System.Drawing.Icon(icoPath);
        }
        catch { /* Static context — cannot call Log; fall back to system icon. */ }
        return System.Drawing.SystemIcons.Application;
    }


    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PC Volume Controller",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };

        _trayIcon.ContextMenuStrip.Items.Add("Open Dashboard", null, (_, _) => DispatchUi(RestoreFromTray));
        _trayIcon.ContextMenuStrip.Items.Add("Connect", null, (_, _) => DispatchUi(() =>
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            if (!_serialService.IsConnected)
            {
                ConnectSerial();
            }
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Disconnect", null, (_, _) => DispatchUi(() =>
        {
            RequestManualDisconnect("Tray disconnect requested");
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Reconnect", null, (_, _) => DispatchUi(() =>
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            Log("Tray reconnect requested.");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: true);
            TryAutoReconnect(GetAvailableComPorts());
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Open Log Folder", null, (_, _) => DispatchUi(OpenLogFolder));
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => DispatchUi(ExitApplication));
        _trayIcon.DoubleClick += (_, _) => DispatchUi(RestoreFromTray);

        BuildTrayProfileMenu();
    }


    private void BuildTrayProfileMenu()
    {
        if (_trayIcon?.ContextMenuStrip == null) return;

        // Remove the old profile menu item if it exists
        if (_trayProfileMenuItem != null)
        {
            _trayIcon.ContextMenuStrip.Items.Remove(_trayProfileMenuItem);
            _trayProfileMenuItem.Dispose();
            _trayProfileMenuItem = null;
        }

        if (_settings.Profiles.Count <= 1) return; // no submenu needed for single profile

        _trayProfileMenuItem = new Forms.ToolStripMenuItem("Switch Profile");

        foreach (ProfileEntry profile in _settings.Profiles)
        {
            string profileName = profile.Name;
            var item = new Forms.ToolStripMenuItem(profileName)
            {
                Checked = profileName == _settings.ActiveProfileName
            };
            item.Click += (_, _) => Dispatcher.InvokeAsync(() => SwitchToProfile(profileName));
            _trayProfileMenuItem.DropDownItems.Add(item);
        }

        // Insert before the last separator/exit item (index 2 = after Open and Connect)
        int insertIndex = Math.Min(2, _trayIcon.ContextMenuStrip.Items.Count);
        _trayIcon.ContextMenuStrip.Items.Insert(insertIndex, _trayProfileMenuItem);
    }


    private void DispatchUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.InvokeAsync(action);
        }
    }


    private void ShowTrayNotification(string title, string message, int timeoutMs = 3000)
    {
        try
        {
            if (_trayIcon == null || !_trayIcon.Visible)
            {
                return;
            }

            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(timeoutMs);
        }
        catch
        {
        }
    }


    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        bool minimizeToTray = MinimizeToTrayCheckBox?.IsChecked == true || _settings.MinimizeToTray;

        if (WindowState == WindowState.Minimized && minimizeToTray)
        {
            HideToTray();
        }
    }


    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }


    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }


    private void ExitApplication()
    {
        _reallyClose = true;
        Close();
    }


    protected override void OnClosing(CancelEventArgs e)
    {
        FlushUiToSettings();

        bool minimizeToTray = MinimizeToTrayCheckBox?.IsChecked == true || _settings.MinimizeToTray;

        if (!_reallyClose && minimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _overlayWindow?.HideImmediate();

        base.OnClosing(e);
    }

}
