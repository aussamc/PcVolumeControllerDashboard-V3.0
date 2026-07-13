using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Debug tab — a developer/troubleshooting surface gated behind the
/// <c>AdvancedDebugFeatures</c> setting (or the <c>--debug</c> startup flag). It
/// hosts three sections (parity items Q4/Q6/N3 folded into one tab per decision D2):
///
/// • A live serial console showing every raw line crossing the link in both
///   directions (TX/RX), plus a raw-send box and quick-command buttons.
/// • A hardware self-test (Q4): a per-channel checklist that tallies encoder turns
///   and button presses so the user can confirm all six encoders/buttons register,
///   with Reset and Sleep/Wake test buttons.
/// • A diagnostics readout (Q6): connection/port/heartbeat/protocol/channel-count
///   detail (the colour-coded mismatch warning itself lives on the Audio tab's
///   status line). Plus log helper buttons (N3).
///
/// Traffic events fire on background threads, so they're queued and drained on a
/// UI timer (batching bursts). The view is only rebuilt when new lines arrive (so
/// a text selection survives idle); Pause freezes the view for copying.
/// </summary>
public partial class MainWindow : Window
{
    private const int DebugMaxLines = 1000;
    private const int DebugFlushMs = 100;
    private const int DiagnosticsRefreshMs = 1000;

    private readonly ConcurrentQueue<SerialTraffic> _pendingTraffic = new();
    private readonly List<string> _debugBuffer = new();
    private DispatcherTimer? _debugFlushTimer;
    private bool _debugForceRefresh;

    // --debug force-shows the Debug tab for this session regardless of the setting.
    private bool _forceDebugTab;

    // Q4 hardware self-test tally (Core, unit-tested). Fed from the connection's
    // MessageReceived stream; rendered into the per-channel checklist.
    private readonly HardwareSelfTest _selfTest = new(SerialConnectionService.ExpectedChannelCount);

    // Q6 diagnostics: last inbound line / last CHSTATE|STATE we sent, captured off the
    // traffic stream (on the UI thread while draining), plus a periodic refresh timer
    // so the heartbeat age keeps ticking.
    private DispatcherTimer? _diagTimer;
    private string? _lastRxLine;
    private DateTime? _lastRxTime;
    private string? _lastStateSentLine;

    private void InitDebugTab()
    {
        if (_connection != null)
        {
            _connection.TrafficLogged += OnSerialTraffic;
            _connection.MessageReceived += OnSelfTestMessage;
        }

        _debugFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebugFlushMs) };
        _debugFlushTimer.Tick += (_, _) => FlushDebug();
        _debugFlushTimer.Start();

        _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DiagnosticsRefreshMs) };
        _diagTimer.Tick += (_, _) => UpdateDiagnostics();
        _diagTimer.Start();

        UpdateSelfTest();
        UpdateDiagnostics();
        UpdateDebugTabVisibility();
    }

    /// <summary>
    /// Shows or hides the entire Debug tab to match the <c>AdvancedDebugFeatures</c>
    /// setting, with <c>--debug</c> forcing it on for the session. If the tab is
    /// hidden while selected, selection falls back to the first tab so the view isn't
    /// left blank.
    /// </summary>
    private void UpdateDebugTabVisibility()
    {
        bool show = _settings.AdvancedDebugFeatures || _forceDebugTab;
        DebugTab.IsVisible = show;
        if (!show && ReferenceEquals(MainTabs.SelectedItem, DebugTab))
            MainTabs.SelectedIndex = 0;
    }

    // Fires on any thread — just enqueue; the UI timer drains it.
    private void OnSerialTraffic(SerialTraffic t) => _pendingTraffic.Enqueue(t);

    // MessageReceived fires on the serial read thread — marshal to the UI thread
    // before touching the (non-thread-safe) tally and updating the checklist.
    private void OnSelfTestMessage(DeviceMessage msg) =>
        Dispatcher.UIThread.Post(() => { if (_selfTest.Record(msg)) UpdateSelfTest(); });

    private void FlushDebug()
    {
        bool any = false;
        while (_pendingTraffic.TryDequeue(out SerialTraffic t))
        {
            _debugBuffer.Add($"{t.Time:HH:mm:ss.fff}  {(t.Outgoing ? "TX" : "RX")}  {t.Line}");
            CaptureDiagnosticsLine(t);
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

    // Remembers the most recent inbound line (for "last ESP32 msg") and the most
    // recent CHSTATE/STATE we sent (for "last state sent"), driving the Q6 readout
    // without reaching into the services. Runs on the UI thread (from FlushDebug).
    private void CaptureDiagnosticsLine(SerialTraffic t)
    {
        if (t.Outgoing)
        {
            if (t.Line.StartsWith(ProtocolCommands.ChannelState + ",", StringComparison.Ordinal) ||
                t.Line.StartsWith(ProtocolCommands.State + ",", StringComparison.Ordinal))
                _lastStateSentLine = t.Line;
        }
        else
        {
            _lastRxLine = t.Line;
            _lastRxTime = t.Time;
        }
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

    private void DebugSend_Click(object? sender, RoutedEventArgs e)
    {
        string line = (DebugSendBox.Text ?? string.Empty).Trim();
        if (line.Length > 0 && SendDebugLine(line)) DebugSendBox.Text = string.Empty;
    }

    private void DebugSend_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string line = (DebugSendBox.Text ?? string.Empty).Trim();
        if (line.Length > 0 && SendDebugLine(line)) DebugSendBox.Text = string.Empty;
    }

    private void DebugQuick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command } && command.Length > 0)
            SendDebugLine(command);
    }

    // Commands that hijack the OLEDs and leave them frozen until new state arrives;
    // we redraw the normal screens a few seconds later by forcing a state re-push.
    private static readonly string[] ScreenHijackCommands = { "SHOW_IDENT", "TEST_DISPLAY" };
    private const int IdentRevertMs = 10_000;
    private DispatcherTimer? _identRevertTimer;

    private bool SendDebugLine(string line)
    {
        bool sent = _connection?.SendLine(line) ?? false;
        DebugSendStatus.Text = sent ? string.Empty : "Not connected — can't send.";
        if (!sent) return false;

        if (Array.Exists(ScreenHijackCommands, c => string.Equals(c, line, StringComparison.OrdinalIgnoreCase)))
            ScheduleOledRevert();
        return true;
    }

    // Restartable one-shot: redraw the OLEDs IdentRevertMs after the last hijack command.
    private void ScheduleOledRevert()
    {
        _identRevertTimer ??= new DispatcherTimer();
        _identRevertTimer.Stop();
        _identRevertTimer.Interval = TimeSpan.FromMilliseconds(IdentRevertMs);
        _identRevertTimer.Tick -= OnIdentRevertTick;
        _identRevertTimer.Tick += OnIdentRevertTick;
        _identRevertTimer.Start();
    }

    private void OnIdentRevertTick(object? sender, EventArgs e)
    {
        _identRevertTimer?.Stop();
        _deviceState?.ForceResend(); // next poll (≤500ms) re-pushes CHSTATE, redrawing the OLEDs
    }

    // ── Q4 hardware self-test ─────────────────────────────────────────────────

    private void UpdateSelfTest() => SelfTestText.Text = _selfTest.FormatAll();

    private void SelfTestReset_Click(object? sender, RoutedEventArgs e)
    {
        _selfTest.Reset();
        UpdateSelfTest();
        SelfTestStatusText.Text = "Self-test tally cleared.";
    }

    // Sleep/Wake test: route through the same DeviceStateService suppression the
    // real auto sleep/wake path uses (SetControllerAsleep), otherwise the 50ms
    // channel-state poll would immediately re-push CHSTATE and wake the OLEDs back
    // up before the user can see the effect.
    private void SelfTestSleep_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection?.State != SerialConnectionState.Connected)
        {
            SelfTestStatusText.Text = "Not connected — can't run the sleep test.";
            return;
        }
        _deviceState?.SetControllerAsleep(true);
        _connection.SendLine($"{ProtocolCommands.Sleep},debug-test", log: true);
        SelfTestStatusText.Text = "Sent SLEEP — the OLEDs should blank.";
    }

    private void SelfTestWake_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection?.State != SerialConnectionState.Connected)
        {
            SelfTestStatusText.Text = "Not connected — can't run the wake test.";
            return;
        }
        _connection.SendLine($"{ProtocolCommands.Wake},debug-test", log: true);
        _deviceState?.SetControllerAsleep(false); // re-enable pushes + force a full resend
        _deviceState?.ForceResend();
        SelfTestStatusText.Text = "Sent WAKE — the OLEDs should repaint.";
    }

    // ── Q6 diagnostics readout ────────────────────────────────────────────────

    private void UpdateDiagnostics()
    {
        SerialConnectionState state = _connection?.State ?? SerialConnectionState.Disconnected;
        DiagConnectionText.Text = state.ToString();
        DiagPortText.Text = string.IsNullOrEmpty(_connection?.PortName) ? "(none)" : _connection!.PortName!;

        DiagHeartbeatText.Text = _lastRxTime is { } rx
            ? $"{(DateTime.Now - rx).TotalSeconds:0.0}s ago"
            : "(no data yet)";

        string protocol = string.IsNullOrEmpty(_connection?.Protocol) ? "(unknown)" : _connection!.Protocol!;
        DiagProtocolText.Text = $"{protocol} (required {RequiredProtocolVersion})";

        int reported = _connection?.ConnectedChannelCount ?? 0;
        DiagChannelsText.Text = $"{reported} reported / {SerialConnectionService.ExpectedChannelCount} expected";

        DiagLastRxText.Text = string.IsNullOrEmpty(_lastRxLine) ? "—" : _lastRxLine!;
        DiagLastStateText.Text = string.IsNullOrEmpty(_lastStateSentLine) ? "—" : _lastStateSentLine!;
    }

    // ── N3 log/console helpers ────────────────────────────────────────────────

    private async void DebugCopyConsole_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, _debugBuffer));
        DebugLogHelperStatus.Text = "Console copied.";
    }

    private async void DebugCopyLogPath_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string dir = App.Services.GetService<LogService>()?.Directory
                     ?? Path.Combine(Path.GetDirectoryName(SettingsService.SettingsPath) ?? ".", "logs");
        await clipboard.SetTextAsync(dir);
        DebugLogHelperStatus.Text = "Log folder path copied.";
    }

    private void DebugOpenLogFile_Click(object? sender, RoutedEventArgs e)
    {
        string? path = App.Services.GetService<LogService>()?.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            DebugLogHelperStatus.Text = "No log file yet.";
            return;
        }
        try
        {
            // UseShellExecute lets the OS open the file with its default handler
            // (Windows shell / xdg-open on Linux / open on macOS).
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            DebugLogHelperStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            DebugLogHelperStatus.Text = $"Couldn't open log file: {ex.Message}";
        }
    }
}
