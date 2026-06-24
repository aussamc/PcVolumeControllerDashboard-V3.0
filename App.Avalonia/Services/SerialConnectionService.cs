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

    private readonly SerialService _serial;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private Timer? _identifyTimer;
    private readonly Queue<string> _candidates = new();

    public SerialConnectionState State { get; private set; } = SerialConnectionState.Disconnected;
    public string? ConnectedChipId { get; private set; }
    public string? Protocol { get; private set; }

    /// <summary>Fired (on any thread) when the connection state changes.</summary>
    public event Action<SerialConnectionState>? StateChanged;

    /// <summary>Fired (on any thread) for each parsed device message while connected.</summary>
    public event Action<DeviceMessage>? MessageReceived;

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

        string[] ports = SerialService.GetPortNames();
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

        BeginScan(candidates);
    }

    /// <summary>Explicitly connects to a single port (e.g. from a future UI).</summary>
    public void Connect(string port) => BeginScan(new List<string> { port });

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
            _log.Log("No controller found on any candidate port.");
            SetState(SerialConnectionState.Disconnected);
            return;
        }

        string port = _candidates.Dequeue();
        try
        {
            if (!_serial.Open(port, BaudRate))
            {
                _log.Log($"Port {port} already open; skipping.");
                TryNextCandidate();
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Log($"Open {port} failed: {ex.Message}; trying next.");
            TryNextCandidate();
            return;
        }

        ConnectedChipId = null;
        Protocol = null;
        SetState(SerialConnectionState.Identifying);
        _log.Log($"Opened {port}; requesting identity.");

        _serial.SendLine(ProtocolCommands.HelloQuery); // "HELLO?"

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
        _serial.SendLine(line);
        if (log) _log.Log($"PC -> ESP32: {line}");
        return true;
    }

    public void Disconnect()
    {
        _identifyTimer?.Dispose();
        _identifyTimer = null;
        _candidates.Clear();
        _serial.Close();
        SetState(SerialConnectionState.Disconnected);
        _log.Log("Disconnected.");
    }

    private void OnIdentifyTimeout(string port)
    {
        if (State != SerialConnectionState.Identifying) return;
        _log.Log($"No valid controller identity from {port} within {IdentifyTimeoutMs} ms.");
        _serial.Close();
        TryNextCandidate(); // advance to the next candidate, or stop if none remain
    }

    private void OnLineReceived(string line)
    {
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

                // Remember the port for next launch's fast reconnect.
                if (_serial.PortName is { Length: > 0 } p && !string.Equals(_settings.Settings.LastComPort, p, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Settings.LastComPort = p;
                    _settings.Save();
                }

                SetState(SerialConnectionState.Connected);
                _log.Log($"Controller identified on {_serial.PortName}: protocol {msg.Protocol}, " +
                         $"{msg.ChannelCount} channels, chip {(string.IsNullOrEmpty(msg.ChipId) ? "(none)" : msg.ChipId)}.");
            }
            else if (msg.Kind != DeviceMessageKind.Debug)
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
        _log.Log($"Serial error: {error}");
        Disconnect();
    }

    private void SetState(SerialConnectionState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged?.Invoke(state); } catch { }
    }

    public void Dispose()
    {
        _identifyTimer?.Dispose();
        _serial.LineReceived -= OnLineReceived;
        _serial.ErrorOccurred -= OnSerialError;
        _serial.Close();
    }
}
