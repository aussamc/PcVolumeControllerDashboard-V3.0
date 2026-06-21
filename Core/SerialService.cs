using System.IO;
using System.IO.Ports;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Owns a single SerialPort instance. Handles open/close, locked writes,
/// and buffered line-by-line reading. Does not contain any reconnect logic
/// or protocol knowledge.
/// </summary>
public sealed class SerialService : IDisposable
{
    private SerialPort? _port;
    private readonly object _lock = new();

    // ─────────────────────────────────────────── state ──

    /// <summary>True while the underlying SerialPort is open.</summary>
    public bool IsConnected => _port?.IsOpen == true;

    /// <summary>The currently open port name, or null if disconnected.</summary>
    public string? PortName { get; private set; }

    // ─────────────────────────────────────────── events ──

    /// <summary>Fired on the ThreadPool for every complete line received from the device.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Fired when a read or write fails; the caller should disconnect.</summary>
    public event Action<string>? ErrorOccurred;

    // ─────────────────────────────────────────── open / close ──

    /// <summary>
    /// Opens the specified port. Returns true on success, false if already open.
    /// Throws on failure (port not found, access denied, etc.) so the caller
    /// can surface the error to the user.
    /// </summary>
    public bool Open(string portName, int baudRate)
    {
        lock (_lock)
        {
            if (_port?.IsOpen == true) return false;

            _port = new SerialPort(portName, baudRate)
            {
                NewLine   = ((char)10).ToString(),
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout  = 100,
                WriteTimeout = 250,
            };

            // Throws if the port does not exist or is in use.
            _port.DataReceived += OnDataReceived;
            _port.Open();

            // Confirm DTR/RTS off after open (some drivers ignore the property
            // set before Open()).
            try { _port.DtrEnable = false; _port.RtsEnable = false; } catch { }

            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            PortName = portName;
            return true;
        }
    }

    /// <summary>
    /// Closes and disposes the underlying port. Safe to call multiple times.
    /// </summary>
    public void Close()
    {
        SerialPort? port;
        lock (_lock)
        {
            port  = _port;
            _port = null;
            PortName = null;
        }

        if (port == null) return;

        port.DataReceived -= OnDataReceived;
        try { if (port.IsOpen) port.Close(); } catch { }
        try { port.Dispose(); }                catch { }
    }

    // ─────────────────────────────────────────── write ──

    /// <summary>
    /// Sends a line to the device. No-ops if the port is not open.
    /// Fires <see cref="ErrorOccurred"/> on write failure.
    /// </summary>
    public void SendLine(string line)
    {
        lock (_lock)
        {
            if (_port?.IsOpen != true) return;
            try
            {
                _port.WriteLine(line);
            }
            catch (Exception ex) when (
                ex is IOException or
                InvalidOperationException or
                UnauthorizedAccessException or
                TimeoutException)
            {
                ErrorOccurred?.Invoke($"Serial write error: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────── static helpers ──

    /// <summary>Returns available COM port names; never throws.</summary>
    public static string[] GetPortNames()
    {
        try   { return SerialPort.GetPortNames(); }
        catch { return Array.Empty<string>(); }
    }

    // ─────────────────────────────────────────── private ──

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port || !port.IsOpen) return;
        try
        {
            while (port.BytesToRead > 0)
            {
                string? line = port.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(line))
                    LineReceived?.Invoke(line);
            }
        }
        catch (TimeoutException) { /* normal under 100 ms timeout */ }
        catch (IOException ex)                  { ErrorOccurred?.Invoke($"Serial read error: {ex.Message}"); }
        catch (UnauthorizedAccessException ex)  { ErrorOccurred?.Invoke($"Serial access error: {ex.Message}"); }
        catch (Exception ex)                    { ErrorOccurred?.Invoke($"Serial read error: {ex.Message}"); }
    }

    // ─────────────────────────────────────────── IDisposable ──

    public void Dispose() => Close();
}
