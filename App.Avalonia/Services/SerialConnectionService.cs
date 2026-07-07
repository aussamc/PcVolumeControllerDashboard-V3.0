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

    /// <summary>
    /// A controller was found and identified by name, but its firmware protocol is
    /// below the required floor, so the strict handshake (standing rule #5) rejects
    /// it. Distinct from <see cref="Disconnected"/> so the UI can explain *why* the
    /// known-incompatible controller won't connect instead of retrying invisibly.
    /// </summary>
    Incompatible,
}

/// <summary>One raw serial line and its direction, for the Debug console.</summary>
public readonly record struct SerialTraffic(DateTime Time, bool Outgoing, string Line);

/// <summary>
/// Details of a recognised controller whose firmware protocol is too old to
/// connect. Surfaced to the UI so the user is told what to fix (update firmware).
/// </summary>
public sealed record IncompatibleControllerInfo(string Port, string Protocol, int ChannelCount);

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

    /// <summary>Lowest controller protocol the strict handshake will accept.</summary>
    public const string MinProtocol = "2.24";
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

    // Per-port reconnect cooldowns (parity with WPF's _rejectedComPorts /
    // _phantomComPorts). Without them the ~3s reconnect scan re-tries every present
    // port each cycle — including a second serial device already known to be the
    // wrong device — burning a full identify timeout on it every time. Two distinct
    // windows, matching WPF's retry semantics:
    //   • Rejected: a port that produced a HELLO with the wrong device name (or no
    //     valid identity at all within the timeout) — confirmed not our controller,
    //     so back off for a long window.
    //   • Phantom: a port that failed to open (busy / disappeared), plus the
    //     remembered controller port when it merely times out identifying — kept on
    //     the short window so a slow-rebooting real device is never locked out long.
    private const int RejectedPortCooldownMs = 5 * 60 * 1000; // 5 min
    private const int PhantomPortCooldownMs = 15 * 1000;       // 15 s

    private readonly SerialService _serial;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private Timer? _identifyTimer;
    private Timer? _monitorTimer;
    private readonly Queue<string> _candidates = new();

    // Port → cooldown-expiry (Environment.TickCount64 ms). Guarded by _cooldownLock
    // because they're touched from the monitor/identify timer threads, the serial
    // read thread (on success), and public UI-thread calls.
    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, long> _rejectedPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _phantomPorts = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Set while <see cref="State"/> is <see cref="SerialConnectionState.Incompatible"/>:
    /// the recognised-but-too-old controller the scan settled on. Null otherwise.
    /// </summary>
    public IncompatibleControllerInfo? IncompatibleController { get; private set; }

    // Accumulates the incompatible controller seen during the in-flight scan;
    // promoted to IncompatibleController if the scan finds nothing better. Reset at
    // the start of each scan so a fresh scan re-evaluates.
    private IncompatibleControllerInfo? _pendingIncompatible;

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
        // An explicit user "try now" clears every port cooldown so a port that was
        // backed off (wrong device / failed open / slow identify) is retried this
        // scan instead of waiting out its window.
        ClearAllPortCooldowns();
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
        ClearPortCooldown(port); // an explicit target overrides any prior cooldown
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

        // Expire cooldowns whose window has passed, and forget any for a port that
        // has since disappeared (so an unplug/replug of the real controller is tried
        // again immediately rather than being held off by a stale cooldown).
        PrunePortCooldowns(ports);

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

        // Drop ports still serving a cooldown so a known-wrong or unopenable port
        // isn't reopened (and re-timed-out) every reconnect cycle.
        candidates.RemoveAll(IsPortInCooldown);

        if (candidates.Count == 0)
        {
            if (!quiet && ports.Length > 0)
                _log.Log("No connectable ports — all present ports are on reconnect cooldown.");
            SetState(SerialConnectionState.Disconnected);
            return;
        }

        _quietScan = quiet;
        _lastScanTicks = Environment.TickCount64;
        BeginScan(candidates);
    }

    private void EnsureMonitor() =>
        _monitorTimer ??= new Timer(_ => OnMonitorTick(), null, MonitorIntervalMs, MonitorIntervalMs);

    // ── Per-port reconnect cooldowns ─────────────────────────────────────────────

    /// <summary>True if <paramref name="port"/> is currently serving either cooldown.</summary>
    private bool IsPortInCooldown(string port)
    {
        long now = Environment.TickCount64;
        lock (_cooldownLock)
            return (_rejectedPorts.TryGetValue(port, out long r) && now < r) ||
                   (_phantomPorts.TryGetValue(port, out long p) && now < p);
    }

    /// <summary>
    /// Drops expired cooldown entries and — crucially — any cooldown for a port that
    /// is no longer present. A physical unplug/replug therefore clears the lockout,
    /// so the real controller reconnects promptly after being re-seated; the cooldown
    /// only suppresses repeated retries while the same wrong/unopenable port stays
    /// plugged in. <paramref name="presentPorts"/> is the current enumeration.
    /// </summary>
    private void PrunePortCooldowns(string[] presentPorts)
    {
        long now = Environment.TickCount64;
        lock (_cooldownLock)
        {
            PruneCooldownMap(_rejectedPorts, presentPorts, now);
            PruneCooldownMap(_phantomPorts, presentPorts, now);
        }
    }

    private static void PruneCooldownMap(Dictionary<string, long> map, string[] presentPorts, long now)
    {
        if (map.Count == 0) return;
        var expired = map
            .Where(kv => now >= kv.Value || !presentPorts.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        foreach (string p in expired)
            map.Remove(p);
    }

    /// <summary>Backs a confirmed-wrong port off for the long (rejected) window.</summary>
    private void MarkPortRejected(string? port, string reason)
    {
        if (string.IsNullOrWhiteSpace(port)) return;
        lock (_cooldownLock)
            _rejectedPorts[port] = Environment.TickCount64 + RejectedPortCooldownMs;
        _log.Log($"Rejected {port}: {reason}. Skipping it for {RejectedPortCooldownMs / 60000} min " +
                 "(or until it is unplugged/replugged or you press Reconnect).");
    }

    /// <summary>Backs an unopenable (or slow remembered) port off for the short window.</summary>
    private void MarkPortPhantom(string? port, string reason)
    {
        if (string.IsNullOrWhiteSpace(port)) return;
        lock (_cooldownLock)
            _phantomPorts[port] = Environment.TickCount64 + PhantomPortCooldownMs;
        if (!_quietScan)
            _log.Log($"Port {port} unavailable: {reason}. Skipping it for {PhantomPortCooldownMs / 1000}s.");
    }

    private void ClearPortCooldown(string? port)
    {
        if (string.IsNullOrWhiteSpace(port)) return;
        lock (_cooldownLock)
        {
            _rejectedPorts.Remove(port);
            _phantomPorts.Remove(port);
        }
    }

    private void ClearAllPortCooldowns()
    {
        lock (_cooldownLock)
        {
            _rejectedPorts.Clear();
            _phantomPorts.Clear();
        }
    }

    /// <summary>True if <paramref name="port"/> is the remembered controller port.</summary>
    private bool IsRememberedPort(string port) =>
        !string.IsNullOrWhiteSpace(_settings.Settings.LastComPort) &&
        string.Equals(_settings.Settings.LastComPort, port, StringComparison.OrdinalIgnoreCase);

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

            // Incompatible is treated like Disconnected for reconnection: keep
            // rescanning so that if the user flashes newer firmware, the next scan
            // identifies it and connects (clearing the warning) with no manual step.
            case SerialConnectionState.Disconnected:
            case SerialConnectionState.Incompatible:
                if (!_autoReconnect || !_settings.Settings.AutoConnectOnLaunch) return;
                if (Environment.TickCount64 - _lastScanTicks < ReconnectThrottleMs) return;
                StartScan(quiet: true);
                break;
        }
    }

    private void BeginScan(IEnumerable<string> candidates)
    {
        _pendingIncompatible = null; // a fresh scan re-evaluates compatibility
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
            // Nothing compatible found. If a recognised-but-too-old controller
            // turned up during the scan, settle into Incompatible (persisting its
            // details for the UI) rather than a bare Disconnected.
            if (_pendingIncompatible is { } incompatible)
            {
                IncompatibleController = incompatible;
                SetState(SerialConnectionState.Incompatible);
                return;
            }

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
            // Open failure (busy / unplugged mid-scan): short cooldown so the next
            // cycle doesn't immediately retry it, but it recovers quickly.
            MarkPortPhantom(port, ex.Message);
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
        _pendingIncompatible = null;
        IncompatibleController = null;
        _serial.Close();
        SetState(SerialConnectionState.Disconnected);
    }

    private void OnIdentifyTimeout(string port)
    {
        if (State != SerialConnectionState.Identifying) return;
        if (!_quietScan) _log.Log($"No valid controller identity from {port} within {IdentifyTimeoutMs} ms.");

        // Remember this port produced no valid identity so the next reconnect cycle
        // skips it instead of burning another full identify timeout on it. The
        // remembered controller port only gets the short (phantom) cooldown — a slow
        // reboot must never lock the real device out for the long rejected window.
        if (IsRememberedPort(port))
            MarkPortPhantom(port, "no valid controller identity within timeout");
        else
            MarkPortRejected(port, "no valid controller identity within timeout");

        _serial.Close();
        TryNextCandidate(); // advance to the next candidate, or stop if none remain
    }

    /// <summary>
    /// Handles a HELLO from our controller whose protocol is below the required
    /// floor: logs it (once per distinct incompatibility, so the every-few-seconds
    /// rescan doesn't spam), remembers it for the UI warning, and rejects the port —
    /// advancing to the next candidate so a compatible controller elsewhere can win.
    /// </summary>
    private void HandleIncompatibleController(DeviceMessage hello)
    {
        string port = _serial.PortName ?? "(unknown)";
        var info = new IncompatibleControllerInfo(port, hello.Protocol, hello.ChannelCount);

        // Log on an explicit (non-quiet) scan, or whenever the incompatibility is
        // new/changed — but stay silent while a background rescan keeps re-finding
        // the same known-incompatible controller.
        if (!_quietScan || !info.Equals(IncompatibleController))
            _log.Log($"Controller on {port} reports protocol {hello.Protocol} " +
                     $"({hello.ChannelCount} channels); requires {MinProtocol}+. " +
                     $"Not connecting — update the controller firmware.");

        _pendingIncompatible = info;

        // Reject off the serial read thread: this runs inside the port's own
        // DataReceived callback, and closing a SerialPort from there can deadlock.
        // A zero-delay timer hops threads, mirroring the identify-timeout path.
        _identifyTimer?.Dispose();
        _identifyTimer = new Timer(_ =>
        {
            if (State != SerialConnectionState.Identifying) return;
            _serial.Close();
            TryNextCandidate();
        }, null, 0, Timeout.Infinite);
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
            // A HELLO from our controller by name but below the protocol floor is
            // rejected (standing rule #5) — but recorded and surfaced, not silently
            // ignored like unrelated serial traffic.
            if (SerialProtocol.IsExpectedDevice(msg, IdentityName) &&
                !SerialProtocol.IsValidIdentity(msg, IdentityName, MinProtocol))
            {
                HandleIncompatibleController(msg);
                return;
            }

            if (msg.Kind == DeviceMessageKind.Hello &&
                SerialProtocol.IsValidIdentity(msg, IdentityName, MinProtocol))
            {
                _identifyTimer?.Dispose();
                _identifyTimer = null;
                _candidates.Clear(); // found it — stop scanning remaining ports
                ClearPortCooldown(_serial.PortName); // a good identify clears any backoff
                IncompatibleController = null; // a good link clears any prior warning
                _pendingIncompatible = null;
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
