using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

public enum SerialConnectionState
{
    Disconnected,
    Identifying,
    Connected,
}

/// <summary>One raw serial line and its direction, for the Debug console.</summary>
public readonly record struct SerialTraffic(DateTime Time, bool Outgoing, string Line);

/// <summary>
/// Drives the serial connection lifecycle for the Avalonia host: opens a port,
/// performs the identity handshake (validating the HELLO), tracks connection
/// state, and surfaces parsed device events (ENC / BTN) once connected. Wraps the
/// platform-agnostic Core <see cref="SerialService"/> and <see cref="SerialProtocol"/>.
///
/// This is the connection half of the runtime backbone. Mapping device events to
/// audio operations (the channel runtime) and the connection UI land next.
/// </summary>
public sealed class SerialConnectionService : IDisposable
{
    private const string IdentityName = "PC_VOLUME_CONTROLLER";
    private const string MinProtocol = "2.24";
    private const int BaudRate = 115200;
    private const int IdentifyTimeoutMs = 4000;

    // The monitor runs once a second. While connected it sends a PING (the
    // firmware replies PONG, refreshing both its watchdog and our liveness clock)
    // and declares the link dead if no inbound line has arrived within the
    // liveness timeout — this catches an unplug that doesn't surface as a write
    // error. While disconnected it re-scans for the controller, throttled so a
    // missing device doesn't get hammered.
    private const int MonitorIntervalMs = 1000;
    private const int LivenessTimeoutMs = 3000;
    private const int ReconnectThrottleMs = 3000;

    private readonly SerialService _serial;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private Timer? _identifyTimer;
    private Timer? _monitorTimer;
    private readonly Queue<string> _candidates = new();

    // Armed while we should keep (re)connecting automatically. A hardware drop
    // leaves this true so the monitor re-establishes the link when the controller
    // returns; an explicit user Disconnect() clears it.
    private volatile bool _autoReconnect;

    // Suppresses the verbose per-port scan logging during background reconnect
    // attempts so a disconnected controller doesn't spam the log every few seconds.
    private bool _quietScan;

    // Liveness/throttle clocks (Environment.TickCount64 milliseconds).
    private long _lastRxTicks;
    private long _lastScanTicks;

    public SerialConnectionState State { get; private set; } = SerialConnectionState.Disconnected;
    public string? ConnectedChipId { get; private set; }
    public string? Protocol { get; private set; }

    /// <summary>Fired (on any thread) when the connection state changes.</summary>
    public event Action<SerialConnectionState>? StateChanged;

    /// <summary>Fired (on any thread) for each parsed device message while connected.</summary>
    public event Action<DeviceMessage>? MessageReceived;

    /// <summary>
    /// Fired (on any thread) for every raw line crossing the link in either
    /// direction — including scan/keepalive traffic and pre-identity device output.
    /// Drives the Debug tab's live serial console. Subscribers must marshal to the
    /// UI thread.
    /// </summary>
    public event Action<SerialTraffic>? TrafficLogged;

    public SerialConnectionService(SerialService serial, SettingsService settings, LogService log)
    {
        _serial = serial;
        _settings = settings;
        _log = log;
        _serial.LineReceived += OnLineReceived;
        _serial.ErrorOccurred += OnSerialError;
    }

    /// <summary>
    /// Auto-connects on launch when enabled: tries the remembered port, then (if
    /// allowed) the first other available port. No-op if auto-connect is off.
    /// </summary>
    public void AutoConnect()
    {
        if (!_settings.Settings.AutoConnectOnLaunch)
        {
            _log.Log("Auto-connect disabled; idle.");
            return;
        }

        _autoReconnect = true;
        EnsureMonitor();
        StartScan(quiet: false);
    }

    /// <summary>
    /// Manual (re)connect from the UI: arms auto-reconnect and scans for the
    /// controller now, regardless of the auto-connect-on-launch preference. Clears
    /// any prior user Disconnect().
    /// </summary>
    public void Reconnect()
    {
        _autoReconnect = true;
        EnsureMonitor();
        // If a controller is already connected (or mid-identify), close the port
        // first. Otherwise the rescan tries to Open() a port that's already open,
        // skips every candidate, and gets stuck "searching" until the device is
        // unplugged — this is what the wizard's "Scan again" hit. Closing lets the
        // rescan reopen and re-identify cleanly.
        if (State != SerialConnectionState.Disconnected)
            ClosePort();
        StartScan(quiet: false);
    }

    /// <summary>Explicitly connects to a single port (e.g. from a future UI).</summary>
    public void Connect(string port)
    {
        _autoReconnect = true;
        EnsureMonitor();
        _quietScan = false;
        _lastScanTicks = Environment.TickCount64;
        BeginScan(new List<string> { port });
    }

    /// <summary>
    /// Builds the candidate port list (remembered first, then the rest when
    /// scanning is enabled) and begins a connection scan. When <paramref name="quiet"/>
    /// is set, per-port progress is not logged — used for background reconnect ticks.
    /// </summary>
    private void StartScan(bool quiet)
    {
        string[] ports = SerialService.GetPortNames();
        if (!quiet)
            _log.Log($"Auto-connect: available ports = {(ports.Length == 0 ? "(none)" : string.Join(", ", ports))}.");

        // Candidate order: remembered port first (fast path), then — when scanning
        // is enabled (or nothing is remembered) — every other port as a fallback.
        var candidates = new List<string>();
        string remembered = _settings.Settings.LastComPort;
        bool rememberedPresent = !string.IsNullOrWhiteSpace(remembered) &&
                                 ports.Contains(remembered, StringComparer.OrdinalIgnoreCase);
        if (rememberedPresent)
            candidates.Add(remembered);

        if (_settings.Settings.ScanAllComPortsIfRememberedMissing || !rememberedPresent)
        {
            foreach (string p in ports)
                if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(p);
        }

        if (candidates.Count == 0)
        {
            SetState(SerialConnectionState.Disconnected);
            return;
        }

        _quietScan = quiet;
        _lastScanTicks = Environment.TickCount64;
        BeginScan(candidates);
    }

    private void EnsureMonitor() =>
        _monitorTimer ??= new Timer(_ => OnMonitorTick(), null, MonitorIntervalMs, MonitorIntervalMs);

    /// <summary>
    /// Once-a-second connection monitor. Connected: PING for keepalive and drop
    /// the link if it has gone silent past the liveness timeout (catches an unplug
    /// that doesn't raise a write error). Disconnected: re-scan for the controller,
    /// throttled, so it reconnects on its own after a replug. Identifying: a scan
    /// is already in flight, so wait.
    /// </summary>
    private void OnMonitorTick()
    {
        switch (State)
        {
            case SerialConnectionState.Connected:
                if (Environment.TickCount64 - _lastRxTicks > LivenessTimeoutMs)
                {
                    ClosePort();
                    _log.Log("Connection lost (no response from controller); will attempt to reconnect.");
                    return;
                }
                Send(ProtocolCommands.Ping); // firmware replies PONG
                break;

            case SerialConnectionState.Disconnected:
                if (!_autoReconnect || !_settings.Settings.AutoConnectOnLaunch) return;
                if (Environment.TickCount64 - _lastScanTicks < ReconnectThrottleMs) return;
                StartScan(quiet: true);
                break;
        }
    }

    private void BeginScan(IEnumerable<string> candidates)
    {
        _candidates.Clear();
        foreach (string p in candidates)
            _candidates.Enqueue(p);
        TryNextCandidate();
    }

    /// <summary>Opens the next candidate port and begins its identity handshake.</summary>
    private void TryNextCandidate()
    {
        if (_candidates.Count == 0)
        {
            if (!_quietScan) _log.Log("No controller found on any candidate port.");
            SetState(SerialConnectionState.Disconnected);
            return;
        }

        string port = _candidates.Dequeue();
        try
        {
            if (!_serial.Open(port, BaudRate))
            {
                if (!_quietScan) _log.Log($"Port {port} already open; skipping.");
                TryNextCandidate();
                return;
            }
        }
        catch (Exception ex)
        {
            if (!_quietScan) _log.Log($"Open {port} failed: {ex.Message}; trying next.");
            TryNextCandidate();
            return;
        }

        ConnectedChipId = null;
        Protocol = null;
        SetState(SerialConnectionState.Identifying);
        if (!_quietScan) _log.Log($"Opened {port}; requesting identity.");

        Send(ProtocolCommands.HelloQuery); // "HELLO?"

        _identifyTimer?.Dispose();
        _identifyTimer = new Timer(_ => OnIdentifyTimeout(port), null, IdentifyTimeoutMs, Timeout.Infinite);
    }

    /// <summary>
    /// Sends a protocol line to the controller when connected. No-op (returns
    /// false) otherwise, so callers can fire state pushes unconditionally. When
    /// <paramref name="log"/> is set the outgoing line is written to the log
    /// (reserve this for low-frequency pushes; per-poll spam should stay quiet).
    /// </summary>
    public bool SendLine(string line, bool log = false)
    {
        if (State != SerialConnectionState.Connected) return false;
        Send(line);
        if (log) _log.Log($"PC -> ESP32: {line}");
        return true;
    }

    /// <summary>Writes a raw line to the port and mirrors it to the traffic console.</summary>
    private void Send(string line)
    {
        _serial.SendLine(line);
        RaiseTraffic(outgoing: true, line);
    }

    private void RaiseTraffic(bool outgoing, string line)
    {
        try { TrafficLogged?.Invoke(new SerialTraffic(DateTime.Now, outgoing, line)); } catch { }
    }

    /// <summary>User-initiated disconnect: closes the port and disarms automatic
    /// reconnection until the next explicit Connect/AutoConnect.</summary>
    public void Disconnect()
    {
        _autoReconnect = false;
        ClosePort();
        _log.Log("Disconnected.");
    }

    /// <summary>Closes the port and marks the link disconnected, leaving the
    /// auto-reconnect arming untouched (so a hardware drop keeps retrying).</summary>
    private void ClosePort()
    {
        _identifyTimer?.Dispose();
        _identifyTimer = null;
        _candidates.Clear();
        _serial.Close();
        SetState(SerialConnectionState.Disconnected);
    }

    private void OnIdentifyTimeout(string port)
    {
        if (State != SerialConnectionState.Identifying) return;
        if (!_quietScan) _log.Log($"No valid controller identity from {port} within {IdentifyTimeoutMs} ms.");
        _serial.Close();
        TryNextCandidate(); // advance to the next candidate, or stop if none remain
    }

    private void OnLineReceived(string line)
    {
        // Any inbound line proves the controller is alive (PONG replies to our
        // keepalive PING included), refreshing the liveness clock the monitor reads.
        _lastRxTicks = Environment.TickCount64;
        RaiseTraffic(outgoing: false, line);

        DeviceMessage msg = SerialProtocol.Parse(line);

        if (State != SerialConnectionState.Connected)
        {
            if (msg.Kind == DeviceMessageKind.Hello &&
                SerialProtocol.IsValidIdentity(msg, IdentityName, MinProtocol))
            {
                _identifyTimer?.Dispose();
                _identifyTimer = null;
                _candidates.Clear(); // found it — stop scanning remaining ports
                ConnectedChipId = msg.ChipId;
                Protocol = msg.Protocol;

                // Remember the port for next launch's fast reconnect, and auto-pair
                // the controller's chip ID on first identification so the Setup tab
                // shows the paired controller (and future mismatch checks have a baseline).
                bool settingsChanged = false;
                if (_serial.PortName is { Length: > 0 } p && !string.Equals(_settings.Settings.LastComPort, p, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Settings.LastComPort = p;
                    settingsChanged = true;
                }
                if (!string.IsNullOrEmpty(msg.ChipId) && string.IsNullOrEmpty(_settings.Settings.LastDeviceChipId))
                {
                    _settings.Settings.LastDeviceChipId = msg.ChipId;
                    settingsChanged = true;
                }
                if (settingsChanged) _settings.Save();

                SetState(SerialConnectionState.Connected);
                _log.Log($"Controller identified on {_serial.PortName}: protocol {msg.Protocol}, " +
                         $"{msg.ChannelCount} channels, chip {(string.IsNullOrEmpty(msg.ChipId) ? "(none)" : msg.ChipId)}.");
            }
            else if (msg.Kind != DeviceMessageKind.Debug && !_quietScan)
            {
                _log.Log($"Ignoring pre-identity data: {msg.Raw}");
            }
            return;
        }

        switch (msg.Kind)
        {
            case DeviceMessageKind.EncoderTurn:
                _log.Log($"ENC ch{msg.Channel} delta {msg.Delta}");
                break;
            case DeviceMessageKind.ButtonShort:
            case DeviceMessageKind.ButtonLong:
            case DeviceMessageKind.ButtonDouble:
                _log.Log($"{msg.Kind} ch{msg.Channel}");
                break;
        }

        if (msg.Kind != DeviceMessageKind.Unknown && msg.Kind != DeviceMessageKind.Debug)
            MessageReceived?.Invoke(msg);
    }

    private void OnSerialError(string error)
    {
        // Treat as a connection drop (e.g. the controller was unplugged). Keep
        // auto-reconnect armed so the watchdog re-establishes the link when it
        // returns — only an explicit user Disconnect() disarms reconnection.
        bool wasConnected = State == SerialConnectionState.Connected;
        ClosePort();
        if (wasConnected)
            _log.Log($"Connection lost ({error}); will attempt to reconnect.");
    }

    private void SetState(SerialConnectionState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged?.Invoke(state); } catch { }
    }

    public void Dispose()
    {
        _autoReconnect = false;
        _monitorTimer?.Dispose();
        _identifyTimer?.Dispose();
        _serial.LineReceived -= OnLineReceived;
        _serial.ErrorOccurred -= OnSerialError;
        _serial.Close();
    }
}
