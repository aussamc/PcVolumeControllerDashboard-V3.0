using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Debug tab — a live serial console. Shows every raw line crossing the link in
/// both directions (TX/RX), including scan/keepalive traffic and the controller's
/// pre-identity boot output, and lets you send a raw command. Useful while
/// bringing up or testing the hardware.
///
/// Traffic events fire on background threads, so they're queued and drained on a
/// UI timer (batching bursts). The view is only rebuilt when new lines arrive (so
/// a text selection survives idle); Pause freezes the view for copying.
/// </summary>
public partial class MainWindow : Window
{
    private const int DebugMaxLines = 1000;
    private const int DebugFlushMs = 100;

    private readonly ConcurrentQueue<SerialTraffic> _pendingTraffic = new();
    private readonly List<string> _debugBuffer = new();
    private DispatcherTimer? _debugFlushTimer;
    private bool _debugForceRefresh;

    private void InitDebugTab()
    {
        if (_connection != null)
            _connection.TrafficLogged += OnSerialTraffic;

        _debugFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebugFlushMs) };
        _debugFlushTimer.Tick += (_, _) => FlushDebug();
        _debugFlushTimer.Start();
    }

    // Fires on any thread — just enqueue; the UI timer drains it.
    private void OnSerialTraffic(SerialTraffic t) => _pendingTraffic.Enqueue(t);

    private void FlushDebug()
    {
        bool any = false;
        while (_pendingTraffic.TryDequeue(out SerialTraffic t))
        {
            _debugBuffer.Add($"{t.Time:HH:mm:ss.fff}  {(t.Outgoing ? "TX" : "RX")}  {t.Line}");
            any = true;
        }

        if (any && _debugBuffer.Count > DebugMaxLines)
            _debugBuffer.RemoveRange(0, _debugBuffer.Count - DebugMaxLines);

        if (DebugPauseCheck.IsChecked == true) return;       // freeze the view while paused
        if (!any && !_debugForceRefresh) return;             // nothing new → keep selection intact
        _debugForceRefresh = false;

        DebugConsoleText.Text = string.Join("\n", _debugBuffer);

        if (DebugAutoScrollCheck.IsChecked == true)
            Dispatcher.UIThread.Post(
                () => DebugScroll.Offset = new Vector(0, DebugScroll.Extent.Height),
                DispatcherPriority.Background);
    }

    private void DebugPause_Changed(object? sender, RoutedEventArgs e)
    {
        // Repaint once when unpausing so the view catches up to the buffer.
        if (DebugPauseCheck.IsChecked != true)
            _debugForceRefresh = true;
    }

    private void DebugClear_Click(object? sender, RoutedEventArgs e)
    {
        _debugBuffer.Clear();
        DebugConsoleText.Text = string.Empty;
    }

    private void DebugSend_Click(object? sender, RoutedEventArgs e) => SendDebugCommand();

    private void DebugSend_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendDebugCommand();
    }

    private void SendDebugCommand()
    {
        string line = (DebugSendBox.Text ?? string.Empty).Trim();
        if (line.Length == 0) return;

        bool sent = _connection?.SendLine(line) ?? false;
        DebugSendStatus.Text = sent ? string.Empty : "Not connected — can't send.";
        if (sent) DebugSendBox.Text = string.Empty;
    }
}
