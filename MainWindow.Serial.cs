// MainWindow.Serial.cs — Serial connection lifecycle, COM port management,
// device message handling, and state/OLED sends to the ESP32 controller.
// Extracted from MainWindow.xaml.cs in v2.42. All fields remain in MainWindow.xaml.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WpfBrush = System.Windows.Media.Brush;

namespace PcVolumeControllerDashboard;

public partial class MainWindow
{

    private void QueueDebouncedDeviceChangeRefresh(string reason)
    {
        // Windows can report the device-change event before the COM port has
        // actually reappeared. Do not clear remembered-port cooldowns here; the
        // delayed refresh will clear them only after SerialPort.GetPortNames()
        // confirms the remembered controller port is present.
        if (System.Threading.Interlocked.Exchange(ref _deviceChangeRefreshQueued, 1) == 1)
        {
            return;
        }

        Log($"{reason} received. Scheduling one debounced COM-port refresh.");
        QueueDelayedComPortRefresh("Windows device change debounced refresh");
    }


    private void QueueDelayedComPortRefresh(string reason)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(DeviceChangeDebounceMs);

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Re-check immediately before refreshing. This catches the case
                    // where the device-change burst happened before Windows finished
                    // recreating the COM port.
                    UpdateRememberedControllerPortPresence(reason);
                    UpdateComPortAndConnectionState(forceRefresh: true, reason: reason);
                });
            }
            catch
            {
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _deviceChangeRefreshQueued, 0);
            }
        });
    }


    private void QueueStatePollTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _statePollBusy, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    RefreshAllChannelStates();
                    UpdateControllerPowerStateFromPcActivity();
                    SendStateIfChanged();
                    UpdateDiagnostics();
                }
                catch (Exception ex)
                {
                    Log($"State poll error: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _statePollBusy, 0);
                }
            });
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _statePollBusy, 0);
        }
    }


    private void QueueComPortRefreshTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 1) == 1)
        {
            return;
        }

        // Enumerate COM ports on the background thread to avoid blocking the UI thread.
        // GetPortNames() can be slow on systems with many COM drivers.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string[] ports = SerialService.GetPortNames()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(NormalizeComPortForSort)
                    .ToArray();

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RefreshComPortsIfChanged(portsHint: ports);
                    }
                    catch (Exception ex)
                    {
                        Log($"COM refresh error: {ex.Message}");
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 0);
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"COM port enumeration error: {ex.Message}");
                System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 0);
            }
        });
    }


    private void RequestManualDisconnect(string source)
    {
        _manualDisconnectRequested = true;
        _manualAutoReconnectSuppressionLogged = false;
        _lastManualDisconnectAt = DateTime.Now;
        _lastAutoReconnectAttempt = DateTime.Now;
        _rejectedComPorts.Clear();
        _phantomComPorts.Clear();
        Log($"{source}. Auto-reconnect paused for this dashboard session until Connect/Reconnect is requested.");
        DisconnectSerial();
        SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
    }


    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
    {
        CheckConnectedPortStillExists();
        ForceRefreshComPorts("manual refresh", preserveSelection: false);
    }


    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serialService.IsConnected)
        {
            RequestManualDisconnect("Manual disconnect requested");
        }
        else
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            ConnectSerial();
        }
    }


    private void RefreshComPortsIfChanged(string[]? portsHint = null)
    {
        UpdateComPortAndConnectionState(forceRefresh: false, reason: "auto refresh", portsHint: portsHint);
    }


    private void UpdateComPortAndConnectionState(bool forceRefresh = false, string reason = "auto refresh", string[]? portsHint = null)
    {
        PrunePhantomComPorts();
        UpdateRememberedControllerPortPresence(reason);

        // Use the pre-enumerated port list if one was provided (e.g. from background enumeration).
        string[] ports = portsHint != null
            ? portsHint.Where(p => !IsPortTemporarilyPhantom(p)).ToArray()
            : GetAvailableComPorts();
        string? connectedPort = _serialService.PortName;
        bool serialOpen = _serialService.IsConnected;
        bool connectedPortStillExists = serialOpen && !string.IsNullOrWhiteSpace(connectedPort) &&
            ports.Any(port => port.Equals(connectedPort, StringComparison.OrdinalIgnoreCase));

        bool portListChanged = !_lastKnownComPorts.SequenceEqual(ports, StringComparer.OrdinalIgnoreCase);
        string currentSnapshot = string.Join("|", ComPortComboBox.Items.Cast<object>().Select(item => item.ToString()));
        string newSnapshot = string.Join("|", ports);

        if (serialOpen && !connectedPortStillExists)
        {
            Log($"Connected COM port {connectedPort} is no longer available. Marking controller disconnected.");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            serialOpen = false;
            connectedPort = null;
            ports = GetAvailableComPorts();
            QueueDebouncedDeviceChangeRefresh("connected port disappeared delayed refresh");
        }
        else if (serialOpen && IsConnectedDeviceTimedOut())
        {
            Log("No valid ESP32 identity/heartbeat received within timeout. Marking port disconnected.");
            if (!_esp32HelloReceived)
            {
                MarkPortRejected(connectedPort, "no valid controller identity received");
            }
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            serialOpen = false;
            connectedPort = null;
            ports = GetAvailableComPorts();
            QueueDebouncedDeviceChangeRefresh("controller timeout delayed refresh");
        }

        portListChanged = !_lastKnownComPorts.SequenceEqual(ports, StringComparer.OrdinalIgnoreCase);
        newSnapshot = string.Join("|", ports);
        bool comboNeedsRefresh = forceRefresh || portListChanged || !currentSnapshot.Equals(newSnapshot, StringComparison.OrdinalIgnoreCase);

        if (comboNeedsRefresh)
        {
            ForceRefreshComPorts(reason, preserveSelection: serialOpen, portsOverride: ports);
        }
        else if (!_serialService.IsConnected)
        {
            SetDisconnectedStatusForAvailablePorts(ports);
        }

        if (!serialOpen)
        {
            TryAutoReconnect(ports);
        }

        UpdateDiagnostics();
        UpdateVersionHeader();
    }


    private void ForceRefreshComPorts(string reason, bool preserveSelection, string[]? portsOverride = null)
    {
        string? previousSelection = ComPortComboBox.SelectedItem as string;
        string[] ports = portsOverride ?? GetAvailableComPorts();
        _lastKnownComPorts = ports;

        string? preferred = GetPreferredVisibleComPort(ports, previousSelection, preserveSelection);

        // Aggressive rebuild: the visible dropdown is a pure live view of
        // SerialPort.GetPortNames(). Do not let WPF retain a disconnected
        // port through selection, text search, or an old ItemsSource binding.
        _availableComPorts.Clear();
        foreach (string port in ports)
        {
            _availableComPorts.Add(port);
        }

        ComPortComboBox.SelectedIndex = -1;
        ComPortComboBox.SelectedItem = null;
        ComPortComboBox.SelectedValue = null;
        ComPortComboBox.Text = string.Empty;
        ComPortComboBox.ItemsSource = null;
        ComPortComboBox.Items.Clear();
        ComPortComboBox.ItemsSource = ports.ToList();

        if (!string.IsNullOrWhiteSpace(preferred) &&
            ports.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            string? visiblePort = ports.FirstOrDefault(port =>
                port.Equals(preferred, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(visiblePort))
            {
                ComPortComboBox.SelectedItem = visiblePort;
            }
        }

        if (ports.Length == 0)
        {
            ComPortComboBox.SelectedIndex = -1;
            ComPortComboBox.SelectedItem = null;
            ComPortComboBox.SelectedValue = null;
            ComPortComboBox.Text = string.Empty;
        }

        if (!_serialService.IsConnected)
        {
            SetDisconnectedStatusForAvailablePorts(ports);
        }

        LogComPortRefreshIfChanged(reason, ports, ComPortComboBox.SelectedItem as string);
    }


    private void LogComPortRefreshIfChanged(string reason, string[] ports, string? selectedPort)
    {
        string portSnapshot = ports.Length == 0 ? "none" : string.Join(", ", ports);
        string rememberedPort = string.IsNullOrWhiteSpace(_settings.LastComPort) ? "none" : _settings.LastComPort;
        string selected = string.IsNullOrWhiteSpace(selectedPort) ? "none" : selectedPort;
        string phantomPorts = _phantomComPorts.Count == 0
            ? "none"
            : string.Join(", ", _phantomComPorts.Select(pair => $"{pair.Key} until {pair.Value:HH:mm:ss}"));
        string reasonLower = reason.ToLowerInvariant();

        bool forceLog = reasonLower.Contains("startup") ||
            reasonLower.Contains("manual") ||
            reasonLower.Contains("disconnect") ||
            reasonLower.Contains("device change") ||
            reasonLower.Contains("disappeared") ||
            reasonLower.Contains("error") ||
            reasonLower.Contains("phantom");

        bool changed = !portSnapshot.Equals(_lastLoggedComPortSnapshot, StringComparison.OrdinalIgnoreCase) ||
            !rememberedPort.Equals(_lastLoggedRememberedControllerPort, StringComparison.OrdinalIgnoreCase) ||
            !selected.Equals(_lastLoggedComPortSelection, StringComparison.OrdinalIgnoreCase);

        if (!forceLog && !changed)
        {
            return;
        }

        Log($"COM port refresh ({reason}): actual usable ports = {portSnapshot}; remembered controller port = {rememberedPort}; selected dropdown port = {selected}; phantom/open-failed ports = {phantomPorts}.");

        _lastLoggedComPortSnapshot = portSnapshot;
        _lastLoggedRememberedControllerPort = rememberedPort;
        _lastLoggedComPortSelection = selected;
    }


    private string? GetPreferredVisibleComPort(string[] ports, string? previousSelection, bool preserveSelection)
    {
        if (_serialService.IsConnected &&
            _esp32HelloReceived &&
            !string.IsNullOrWhiteSpace(_serialService.PortName) &&
            ports.Contains(_serialService.PortName, StringComparer.OrdinalIgnoreCase))
        {
            return _serialService.PortName;
        }

        if (preserveSelection &&
            !string.IsNullOrWhiteSpace(previousSelection) &&
            ports.Contains(previousSelection, StringComparer.OrdinalIgnoreCase))
        {
            return previousSelection;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastComPort) &&
            ports.Contains(_settings.LastComPort, StringComparer.OrdinalIgnoreCase))
        {
            return _settings.LastComPort;
        }

        return ports.Length > 0 ? ports[0] : null;
    }


    private void SetDisconnectedStatusForAvailablePorts(string[] ports)
    {
        if (ports.Length == 0)
        {
            SetConnectionStatus("Disconnected - no COM ports found", connected: false);
        }
        else if (!string.IsNullOrWhiteSpace(_settings.LastComPort) && !ports.Contains(_settings.LastComPort, StringComparer.OrdinalIgnoreCase))
        {
            SetConnectionStatus($"Disconnected - waiting for {_settings.LastComPort}", connected: false);
        }
        else
        {
            SetConnectionStatus($"Disconnected - {ports.Length} actual COM port(s) available", connected: false);
        }

        if (!_esp32Seen)
        {
            EspStatusTextBlock.Text = "No controller connected";
        }
    }


    private string[] GetAvailableComPorts()
    {
        PrunePhantomComPorts();

        return GetRawComPorts()
            .Where(port => !IsPortTemporarilyPhantom(port))
            .ToArray();
    }


    private static string[] GetRawComPorts()
    {
        return SerialService.GetPortNames()
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NormalizeComPortForSort)
            .ToArray();
    }


    private static int NormalizeComPortForSort(string portName)
    {
        string digits = new(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int value) ? value : int.MaxValue;
    }


    private bool IsPortTemporarilyPhantom(string portName)
    {
        if (!_phantomComPorts.TryGetValue(portName, out DateTime retryAfter))
        {
            return false;
        }

        if (DateTime.Now >= retryAfter)
        {
            _phantomComPorts.Remove(portName);
            Log($"Phantom/open-failed COM-port cooldown expired for {portName}.");
            return false;
        }

        return true;
    }


    private void PrunePhantomComPorts()
    {
        string[] expiredPorts = _phantomComPorts
            .Where(pair => DateTime.Now >= pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (string port in expiredPorts)
        {
            _phantomComPorts.Remove(port);
            Log($"Phantom/open-failed COM-port cooldown expired for {port}.");
        }
    }


    private void MarkPortPhantom(string? portName, string reason)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        DateTime retryAfter = DateTime.Now.AddMilliseconds(PhantomPortOpenFailCooldownMs);
        _phantomComPorts[portName] = retryAfter;
        Log($"Marked {portName} as phantom/open-failed: {reason}. Will retry after {retryAfter:HH:mm:ss} ({PhantomPortOpenFailCooldownMs / 1000}s cooldown). This cooldown is cleared only after Windows reports the remembered controller port as present again.");
        ForceRefreshComPorts("phantom/open-failed port hidden", preserveSelection: false);
    }


    private void UpdateRememberedControllerPortPresence(string reason)
    {
        string? rememberedPort = string.IsNullOrWhiteSpace(_settings.LastComPort)
            ? null
            : _settings.LastComPort;

        if (string.IsNullOrWhiteSpace(rememberedPort))
        {
            _rememberedPortMissingSince = null;
            return;
        }

        bool rememberedPortIsPresent = GetRawComPorts()
            .Any(port => port.Equals(rememberedPort, StringComparison.OrdinalIgnoreCase));

        if (!rememberedPortIsPresent)
        {
            _rememberedPortMissingSince ??= DateTime.Now;
            return;
        }

        _rememberedPortMissingSince = null;

        if (_phantomComPorts.Remove(rememberedPort))
        {
            _rememberedPortReadyAfter = DateTime.Now.AddMilliseconds(RememberedPortUsbSettleMs);
            Log($"Remembered controller port {rememberedPort} is present again after {reason}. Cleared open-fail cooldown and waiting {RememberedPortUsbSettleMs}ms before opening it.");
        }
    }


    private static bool IsMissingPortOpenFailure(Exception ex)
    {
        string message = ex.Message ?? string.Empty;

        return ex is FileNotFoundException ||
            message.Contains("could not find file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsAccessDeniedOpenFailure(Exception ex)
    {
        string message = ex.Message ?? string.Empty;

        return ex is UnauthorizedAccessException ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access to the path", StringComparison.OrdinalIgnoreCase);
    }


    private void CheckConnectedPortStillExists()
    {
        UpdateComPortAndConnectionState(forceRefresh: true, reason: "connected-port check");
    }


    private bool IsConnectedDeviceTimedOut()
    {
        if (!_serialService.IsConnected)
        {
            return false;
        }

        DateTime now = DateTime.Now;

        if (_lastEspMessageTime.HasValue)
        {
            return (now - _lastEspMessageTime.Value).TotalMilliseconds > DeviceMessageTimeoutMs;
        }

        if (_serialConnectedAt.HasValue)
        {
            return (now - _serialConnectedAt.Value).TotalMilliseconds > HelloTimeoutMs;
        }

        return false;
    }


    private void TryAutoReconnect(string[] ports)
    {
        if (_safeMode)
        {
            return;
        }

        if (!_settings.AutoConnectOnLaunch)
        {
            return;
        }

        if (_manualDisconnectRequested)
        {
            SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
            if (!_manualAutoReconnectSuppressionLogged)
            {
                Log("Auto-connect suppressed because the controller was manually disconnected. Use Connect/Reconnect to resume automatic reconnects.");
                _manualAutoReconnectSuppressionLogged = true;
            }
            return;
        }

        if (_serialService.IsConnected)
        {
            return;
        }

        if ((DateTime.Now - _lastAutoReconnectAttempt).TotalMilliseconds < AutoReconnectCooldownMs)
        {
            return;
        }

        PruneRejectedComPorts();
        UpdateRememberedControllerPortPresence("auto reconnect");

        List<string> candidates = new();
        string? rememberedPort = !string.IsNullOrWhiteSpace(_settings.LastComPort) ? _settings.LastComPort : null;
        bool scanAllPorts = string.IsNullOrWhiteSpace(rememberedPort) || _settings.ScanAllComPortsIfRememberedMissing;

        if (!string.IsNullOrWhiteSpace(rememberedPort))
        {
            string? matchingRememberedPort = ports.FirstOrDefault(port =>
                port.Equals(rememberedPort, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matchingRememberedPort))
            {
                if (DateTime.Now < _rememberedPortReadyAfter)
                {
                    SetConnectionStatus($"Disconnected - waiting for {rememberedPort} to settle", connected: false);
                    if ((DateTime.Now - _lastRememberedPortScanDelayLog).TotalSeconds >= 10)
                    {
                        Log($"Remembered controller port {rememberedPort} is present but still settling after USB re-enumeration; reconnect will retry shortly.");
                        _lastRememberedPortScanDelayLog = DateTime.Now;
                    }
                    return;
                }

                candidates.Add(matchingRememberedPort);
            }
            else
            {
                SetConnectionStatus($"Disconnected - waiting for {rememberedPort}", connected: false);

                if (!scanAllPorts)
                {
                    return;
                }

                TimeSpan missingFor = _rememberedPortMissingSince.HasValue
                    ? DateTime.Now - _rememberedPortMissingSince.Value
                    : TimeSpan.Zero;

                if (missingFor.TotalMilliseconds < RememberedPortMissingBeforeScanAllMs)
                {
                    if ((DateTime.Now - _lastRememberedPortScanDelayLog).TotalSeconds >= 10)
                    {
                        int remainingSeconds = Math.Max(1, (int)Math.Ceiling((RememberedPortMissingBeforeScanAllMs - missingFor.TotalMilliseconds) / 1000.0));
                        Log($"Remembered controller port {rememberedPort} is temporarily missing. Delaying scan-all fallback for {remainingSeconds}s to avoid probing unrelated COM ports during Windows USB churn.");
                        _lastRememberedPortScanDelayLog = DateTime.Now;
                    }
                    return;
                }
            }
        }

        if (scanAllPorts)
        {
            foreach (string port in ports)
            {
                if (!candidates.Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(port);
                }
            }
        }

        string? candidate = candidates.FirstOrDefault(port => !IsPortTemporarilyRejected(port));

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        _lastAutoReconnectAttempt = DateTime.Now;
        Log($"Auto-connect probing {candidate} for controller identity.");
        ConnectSerial(candidate, showErrors: false, isAutoReconnect: true);
    }


    private bool IsPortTemporarilyRejected(string portName)
    {
        if (!_rejectedComPorts.TryGetValue(portName, out DateTime retryAfter))
        {
            return false;
        }

        if (DateTime.Now >= retryAfter)
        {
            _rejectedComPorts.Remove(portName);
            Log($"Rejected-port cooldown expired for {portName}.");
            return false;
        }

        return true;
    }


    private void PruneRejectedComPorts()
    {
        string[] expiredPorts = _rejectedComPorts
            .Where(pair => DateTime.Now >= pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (string port in expiredPorts)
        {
            _rejectedComPorts.Remove(port);
            Log($"Rejected-port cooldown expired for {port}.");
        }
    }


    private void MarkPortRejected(string? portName, string reason)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        DateTime retryAfter = DateTime.Now.AddMilliseconds(RejectedPortCooldownMs);
        _rejectedComPorts[portName] = retryAfter;
        Log($"Rejected {portName}: {reason}. Will retry after {retryAfter:HH:mm:ss} ({RejectedPortCooldownMs / 60000} min cooldown).");
        ShowTrayNotification("Reconnect failed", $"{portName}: {reason}");
    }


    private void ConnectSerial()
    {
        string? selectedPort = ComPortComboBox.SelectedItem as string;
        ConnectSerial(selectedPort, showErrors: true, isAutoReconnect: false);
    }


    private void ConnectSerial(string? portName, bool showErrors, bool isAutoReconnect)
    {
        if (isAutoReconnect && _manualDisconnectRequested)
        {
            SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
            if (!_manualAutoReconnectSuppressionLogged)
            {
                Log("Ignored queued auto-connect attempt because manual disconnect lockout is active.");
                _manualAutoReconnectSuppressionLogged = true;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    "Select a COM port first.",
                    "No COM Port",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (!GetRawComPorts().Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            Log($"Skipped opening {portName} because Windows does not currently list that COM port.");
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    $"{portName} is not currently available. Unplug/replug the controller or select a different COM port.",
                    "COM Port Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MarkPortPhantom(portName, "port not present before open");
            }

            return;
        }

        try
        {
            if (_serialService.IsConnected)
            {
                DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            }

            // Wire events before opening so we never miss the first line.
            _serialService.LineReceived  -= DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred -= BeginDisconnectAfterSerialError;
            _serialService.LineReceived  += DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred += BeginDisconnectAfterSerialError;

            _serialService.Open(portName, BaudRate); // throws on failure
            if (!_serialControlLinesDisabledLogged)
            {
                Log("Serial DTR/RTS kept disabled to avoid ESP32-S3 USB reset loops during connect/disconnect.");
                _serialControlLinesDisabledLogged = true;
            }

            _serialConnectedAt = DateTime.Now;
            _activeConnectionState = isAutoReconnect ? "Auto-identifying" : "Identifying";
            ConnectButton.Content = "Disconnect";
            _esp32Seen = false;
            _esp32HelloReceived = false;
            _espFirmwareName = "Unknown";
            _espProtocolVersion = "Unknown";
            _espChannelCount = "Unknown";
            _connectedDeviceChipId = string.Empty;
            _lastEspMessage = "--";
            _lastEspMessageTime = null;
            SetConnectionStatus($"Identifying controller on {portName}...", connected: false);
            EspStatusTextBlock.Text = "Waiting for controller hello...";
            UpdateVersionHeader();

            Log($"Opened {portName}; waiting for controller identity.");
            RequestHelloFromDevice();
            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Serial Connection Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Log($"Connection error on {portName}: {ex.Message}");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);

            if (IsMissingPortOpenFailure(ex) || IsAccessDeniedOpenFailure(ex))
            {
                MarkPortPhantom(portName, ex.Message);
            }
            else if (!showErrors)
            {
                MarkPortRejected(portName, $"connection error: {ex.Message}");
            }
        }
    }


    private void DisconnectSerial(bool sendDisconnectCommand = true, bool preserveLastControllerPort = true, bool refreshPortsAfterDisconnect = true)
    {
        bool wasAlreadyDisconnected = !_serialService.IsConnected &&
            !_esp32Seen &&
            !_esp32HelloReceived &&
            _activeConnectionState.Equals("Disconnected", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (sendDisconnectCommand)
            {
                SendDisconnectToDevice();
            }

            _serialService.LineReceived  -= DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred -= BeginDisconnectAfterSerialError;
            _serialService.Close();
        }
        catch
        {
        }

        ConnectButton.Content = "Connect";
        _activeConnectionState = "Disconnected";
        _serialConnectedAt = null;
        _controllerSleepRequested = false;
        _controllerSleepReason = string.Empty;
        SetConnectionStatus("Disconnected", connected: false);
        EspStatusTextBlock.Text = "No controller connected";

        _esp32Seen = false;
        _esp32HelloReceived = false;
        _espFirmwareName = "Unknown";
        _espProtocolVersion = "Unknown";
        _espChannelCount = "Unknown";
        _connectedDeviceChipId = string.Empty;
        _lastEspMessage = "--";
        _lastEspMessageTime = null;

        if (refreshPortsAfterDisconnect)
        {
            ForceRefreshComPorts("disconnect", preserveSelection: true);
        }
        UpdateVersionHeader();

        if (!wasAlreadyDisconnected)
        {
            Log("Disconnected.");
        }

        UpdateDiagnostics();
    }


    private void SetConnectionStatus(string text, bool connected)
    {
        ConnectionStatusTextBlock.Text = text;

        string key = connected ? "ConnectionGoodForeground" : "ConnectionBadForeground";

        if (Resources[key] is WpfBrush brush)
        {
            ConnectionStatusTextBlock.Foreground = brush;
        }

        if (!text.Equals(_lastLoggedConnectionState, StringComparison.Ordinal))
        {
            string previousState = _lastLoggedConnectionState;
            Log($"Connection state: {text}");
            _lastLoggedConnectionState = text;

            if (!string.IsNullOrWhiteSpace(previousState))
            {
                if (connected)
                {
                    ShowTrayNotification("Controller connected", text);
                }
                else if (previousState.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    ShowTrayNotification("Controller disconnected", text);
                }
            }
        }

        if (AudioEmptyStatePanel != null)
            AudioEmptyStatePanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        UpdateStatusBar();
    }


    private void UpdateStatusBar()
    {
        if (StatusBarConnectionText == null) return;

        bool connected = _serialService.IsConnected && _esp32HelloReceived;
        StatusBarConnectionText.Text = connected ? "Connected" : "Disconnected";
        StatusBarDot.Fill = connected
            ? (WpfBrush)FindResource("ConnectionGoodForeground")
            : (WpfBrush)FindResource("ConnectionBadForeground");

        StatusBarProfileText.Text = _settings.Profiles.Count > 1
            ? $"Profile: {_settings.ActiveProfileName}"
            : string.Empty;

        string fw = _espProtocolVersion != "Unknown" ? $"Firmware v{_espFirmwareName}" : string.Empty;
        StatusBarFirmwareText.Text = fw;
    }


    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _sessionLocked = true;
                    SendControllerSleep("PC_LOCKED");
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _sessionLocked = false;
                    UpdateControllerPowerStateFromPcActivity(forceEvaluate: true);
                }
            });
        }
        catch
        {
        }
    }


    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    _systemSuspending = true;
                    SendControllerSleep("PC_SUSPEND");
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    _systemSuspending = false;
                    SendControllerWake("PC_RESUME");
                }
            });
        }
        catch
        {
        }
    }


    private void UpdateControllerPowerStateFromPcActivity(bool forceEvaluate = false)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        bool userIdle = GetUserIdleMilliseconds() >= UserIdleSleepMs;
        string reason = _systemSuspending
            ? "PC_SUSPEND"
            : _sessionLocked
                ? "PC_LOCKED"
                : userIdle
                    ? "PC_IDLE"
                    : string.Empty;

        if (userIdle != _lastUserIdleSleepState)
        {
            _lastUserIdleSleepState = userIdle;
            Log(userIdle ? "PC user-idle sleep threshold reached." : "PC user activity detected after idle.");
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            SendControllerSleep(reason);
        }
        else if (!userIdle && (_controllerSleepRequested || forceEvaluate))
        {
            SendControllerWake("PC_ACTIVE");
        }
    }


    private void SendControllerSleep(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        if (_controllerSleepRequested && _controllerSleepReason.Equals(reason, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _controllerSleepRequested = true;
        _controllerSleepReason = reason;
        WriteSerialLine($"{ProtocolCommands.Sleep},{MakeProtocolSafeLabel(reason)}", logOutgoing: true);
        EspStatusTextBlock.Text = $"Connected - controller sleeping ({reason})";
        Log($"Controller sleep requested: {reason}");
        UpdateDiagnostics();
    }


    private void SendControllerWake(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            _controllerSleepRequested = false;
            _controllerSleepReason = string.Empty;
            return;
        }

        if (!_controllerSleepRequested && !reason.Equals("PC_RESUME", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _controllerSleepRequested = false;
        _controllerSleepReason = string.Empty;
        WriteSerialLine($"{ProtocolCommands.Wake},{MakeProtocolSafeLabel(reason)}", logOutgoing: true);
        EspStatusTextBlock.Text = $"Connected - firmware {_espProtocolVersion} ({_espFirmwareName})";
        Log($"Controller wake requested: {reason}");
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
        UpdateDiagnostics();
    }


    private static uint GetUserIdleMilliseconds()
    {
        LASTINPUTINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        return unchecked((uint)Environment.TickCount - info.dwTime);
    }


    private void SendPingToDevice()
    {
        if (_serialService.IsConnected && _esp32HelloReceived)
        {
            WriteSerialLine(ProtocolCommands.Ping, logOutgoing: false);
        }
    }


    private void RequestHelloFromDevice()
    {
        WriteSerialLine(ProtocolCommands.HelloQuery, logOutgoing: false);
    }


    private void SendDisconnectToDevice()
    {
        WriteSerialLine(ProtocolCommands.Disconnect, logOutgoing: false);
    }


    private void BeginDisconnectAfterSerialError(string message)
    {
        Log(message);
        ShowWarning($"Serial connection lost: {message} Reconnection will be attempted automatically.");

        try
        {
            Dispatcher.InvokeAsync(DisconnectSerialDueToError);
        }
        catch
        {
        }
    }


    private void DisconnectSerialDueToError()
    {
        DisconnectSerial(sendDisconnectCommand: false);
        ForceRefreshComPorts("disconnect/error", preserveSelection: true);
    }


    /// <summary>
    /// Marshals a LineReceived callback (ThreadPool) onto the UI thread before
    /// calling HandleDeviceMessage, which touches WPF controls.
    /// </summary>
    private void DispatchHandleDeviceMessage(string line)
        => Dispatcher.InvokeAsync(() => HandleDeviceMessage(line));


    private void HandleDeviceMessage(string line)
    {
        // Guard: discard callbacks that fire after the port has been closed.
        if (!_serialService.IsConnected) return;

        AppendDebugConsole("IN", line);
        string[] parts = line.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length < 1)
        {
            return;
        }

        string command = parts[0].ToUpperInvariant();

        // Update the watchdog timestamp for every message — including pre-HELLO firmware
        // output (I2C scan DBG lines, etc.) — so the connection is not falsely timed out
        // while the firmware is still starting up.
        _lastEspMessage = line;
        _lastEspMessageTime = DateTime.Now;

        if (!_esp32HelloReceived && command != ProtocolCommands.Hello)
        {
            if (!string.Equals(line, ProtocolCommands.Pong, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Ignoring pre-identity serial data from {_serialService.PortName ?? "unknown port"}: {line}");
            }
            return;
        }

        // Normal heartbeat traffic arrives once per second. Keep it out of the log so the log remains useful.
        bool duplicateHelloAfterIdentity = command == ProtocolCommands.Hello &&
            _esp32HelloReceived &&
            _activeConnectionState.Equals("Connected", StringComparison.OrdinalIgnoreCase);

        bool suppressNormalEncoderLog = command == ProtocolCommands.EncoderTurn && !IsAdvancedDebugLoggingEnabled();

        if (!string.Equals(line, ProtocolCommands.Pong, StringComparison.OrdinalIgnoreCase) && !duplicateHelloAfterIdentity && !suppressNormalEncoderLog)
        {
            Log($"ESP32 -> PC: {line}");
        }

        switch (command)
        {
            case ProtocolCommands.Hello:
                HandleHelloMessage(parts, line);
                break;

            case ProtocolCommands.Pong:
                MarkEsp32Seen("heartbeat");
                UpdateDiagnostics();
                break;

            case ProtocolCommands.Debug:
                MarkEsp32Seen("debug");
                Log($"ESP32 debug: {string.Join(",", parts.Skip(1))}");
                break;

            case ProtocolCommands.EncoderTurn:
                MarkEsp32Seen("encoder");
                HandleEncoderMessage(parts);
                break;

            case ProtocolCommands.ButtonLegacy:
            case ProtocolCommands.ButtonShort:
                MarkEsp32Seen("button");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored button event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: button event observed but audio-control action was skipped.");
                    break;
                }
                ApplyShortButtonAction(parts);
                break;

            case ProtocolCommands.ButtonLong:
                MarkEsp32Seen("button long");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored long-button event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: long-button event observed but audio-control action was skipped.");
                    break;
                }
                ApplyLongButtonAction(parts);
                break;

            case ProtocolCommands.ButtonDouble:
                MarkEsp32Seen("button double");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored double-press event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: double-press event observed but audio-control action was skipped.");
                    break;
                }
                ApplyDoubleButtonAction(parts);
                break;

            case ProtocolCommands.Sleeping:
                MarkEsp32Seen("sleep ack");
                if (HardwareTestStatusTextBlock != null)
                {
                    HardwareTestStatusTextBlock.Text = $"Controller sleeping: {string.Join(',', parts.Skip(1))}";
                }
                break;

            case ProtocolCommands.Awake:
                MarkEsp32Seen("wake ack");
                if (HardwareTestStatusTextBlock != null)
                {
                    HardwareTestStatusTextBlock.Text = $"Controller awake: {string.Join(',', parts.Skip(1))}";
                }
                break;

            case ProtocolCommands.OledCfgAck:
                MarkEsp32Seen("oled config ack");
                Log($"ESP32 OLED config applied: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.OledIdleStart:
                MarkEsp32Seen("oled idle start");
                Log($"ESP32 OLED idle started: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.OledIdleEnd:
                MarkEsp32Seen("oled idle end");
                Log($"ESP32 OLED idle ended: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.Error:
                Log($"ESP32 error: {string.Join(',', parts.Skip(1))}");
                break;
        }

        UpdateDiagnostics();
        UpdateVersionHeader();
    }


    private void MarkEsp32Seen(string source)
    {
        _esp32Seen = true;
        _lastEspMessageTime = DateTime.Now;

        if (!_esp32HelloReceived && _serialService.IsConnected)
        {
            EspStatusTextBlock.Text = $"Connected - active, controller hello not received ({source})";
        }
    }


    private void HandleHelloMessage(string[] parts, string rawLine)
    {
        string deviceIdentity = parts.Length > 1 ? parts[1] : "Unknown";
        string protocolVersion = parts.Length > 2 ? parts[2] : "Unknown";
        string channelCount = parts.Length > 3 ? parts[3] : "Unknown";
        // Optional 5th field: ESP32 chip ID (e.g. "0x1234ABCD"). Present only in firmware >= v2.25.
        string chipId = parts.Length > 4 ? parts[4].Trim() : string.Empty;

        if (!deviceIdentity.StartsWith(ExpectedDeviceIdentity, StringComparison.OrdinalIgnoreCase))
        {
            string? badPort = _serialService.PortName;
            Log($"Rejected {badPort}: HELLO identity was '{deviceIdentity}', expected '{ExpectedDeviceIdentity}'.");
            MarkPortRejected(badPort, "wrong device identity");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: true);
            return;
        }

        string connectedPort = _serialService.PortName ?? "unknown port";
        bool alreadyConfirmed = _esp32HelloReceived &&
            _activeConnectionState.Equals("Connected", StringComparison.OrdinalIgnoreCase);

        _esp32Seen = true;
        _esp32HelloReceived = true;
        _espFirmwareName = deviceIdentity;
        _espProtocolVersion = protocolVersion;
        _espChannelCount = channelCount;
        _connectedDeviceChipId = chipId;
        _activeConnectionState = "Connected";
        _manualAutoReconnectSuppressionLogged = false;

        // ── Hardware identity pairing ────────────────────────────────────────────
        if (!string.IsNullOrEmpty(chipId))
        {
            if (string.IsNullOrEmpty(_settings.LastDeviceChipId))
            {
                // First pairing — record the chip ID.
                _settings.LastDeviceChipId = chipId;
                Log($"Controller paired: chip ID {chipId}.");
                SaveSettings();
            }
            else if (!string.Equals(_settings.LastDeviceChipId, chipId, StringComparison.OrdinalIgnoreCase))
            {
                // Chip ID mismatch — different hardware than previously paired.
                Log($"WARNING: Connected controller chip ID ({chipId}) does not match paired controller ({_settings.LastDeviceChipId}). Use 'Forget controller' to re-pair.");
            }
        }

        SetConnectionStatus($"Connected to {connectedPort}", connected: true);
        string chipSuffix = string.IsNullOrEmpty(chipId) ? string.Empty : $", chip {chipId}";
        EspStatusTextBlock.Text = $"Connected - firmware {_espProtocolVersion} ({_espFirmwareName}){chipSuffix}";

        _settings.LastComPort = connectedPort;
        SaveSettings();

        if (alreadyConfirmed)
        {
            UpdateVersionHeader();
            return;
        }

        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
        SendOledSettingsToDevice(logIfNotConnected: false);
        SendAllChannelOledModesToDevice();
        SendPingToDevice();

        UpdateVersionHeader();

        Log($"ESP32 controller confirmed on {connectedPort}. Identity {_espFirmwareName}, protocol {_espProtocolVersion}, channels {_espChannelCount}.");
    }


    private void SendStateIfChanged()
    {
        ChannelMappingItem channel = SelectedChannel;

        string label = MakeProtocolSafeLabel(channel.DisplayLabel);
        int volume = channel.Volume;
        bool muted = channel.Muted;

        if (label != _lastSentLabel || volume != _lastSentVolume || muted != _lastSentMute)
        {
            SendAllChannelStatesToDevice();
            SendStateToDevice(force: true);
        }
    }


    private void SendStateToDevice(bool force = false)
    {
        try
        {
            if (!_serialService.IsConnected || !_esp32HelloReceived || _controllerSleepRequested)
            {
                return;
            }

            ChannelMappingItem channel = SelectedChannel;

            string label = MakeProtocolSafeLabel(channel.DisplayLabel);
            int volume = channel.Volume;
            bool mutedBool = channel.Muted;
            int muted = mutedBool ? 1 : 0;

            if (!force && label == _lastSentLabel && volume == _lastSentVolume && mutedBool == _lastSentMute)
            {
                return;
            }

            string message = $"STATE,{channel.ChannelIndex},{label},{volume},{muted}";

            WriteSerialLine(message, logOutgoing: true);

            _lastSentLabel = label;
            _lastSentVolume = volume;
            _lastSentMute = mutedBool;
            _lastStateSent = message;

            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            Log($"State send error: {ex.Message}");
        }
    }


    private void SendAllChannelStatesToDevice()
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived || _controllerSleepRequested)
        {
            return;
        }

        foreach (ChannelMappingItem channel in _channels)
        {
            string label = MakeProtocolSafeLabel(channel.DisplayLabel);
            string status = MakeProtocolSafeLabel(channel.Status);
            int muted = channel.Muted ? 1 : 0;

            string message = $"{ProtocolCommands.ChannelState},{channel.ChannelIndex},{label},{channel.Volume},{muted},{status}";

            WriteSerialLine(message);
            _lastStateSent = message;
        }

        UpdateDiagnostics();
    }


    private string GetOledDisplayModeProtocolValue()
    {
        return GetDisplayModeFromUi() switch
        {
            DisplayModes.LargeVolume => ProtocolCommands.DisplayModeLargeVolume,
            DisplayModes.MuteStatus => ProtocolCommands.DisplayModeMuteStatus,
            DisplayModes.AppOrDeviceName => ProtocolCommands.DisplayModeAppName,
            DisplayModes.BarPercent => ProtocolCommands.DisplayModeBarPercent,
            _ => ProtocolCommands.DisplayModeAppVolume
        };
    }




    // Converts a dashboard display-mode constant to the firmware protocol string.
    // Empty input → empty output (firmware interprets empty as "use global").
    private static string GetChannelOledModeProtocolValue(string dashboardMode)
    {
        if (string.IsNullOrEmpty(dashboardMode)) return string.Empty;
        return dashboardMode switch
        {
            DisplayModes.LargeVolume     => ProtocolCommands.DisplayModeLargeVolume,
            DisplayModes.MuteStatus      => ProtocolCommands.DisplayModeMuteStatus,
            DisplayModes.AppOrDeviceName => ProtocolCommands.DisplayModeAppName,
            DisplayModes.BarPercent      => ProtocolCommands.DisplayModeBarPercent,
            _                            => ProtocolCommands.DisplayModeAppVolume
        };
    }


    // Gets the per-channel mode index for ChannelOledModeComboBox.
    // Index 0 = Use global default (""), 1-5 = specific modes.
    private static int GetChannelOledModeIndex(string dashboardMode) => dashboardMode switch
    {
        DisplayModes.AppNameAndVolume => 1,
        DisplayModes.LargeVolume      => 2,
        DisplayModes.MuteStatus       => 3,
        DisplayModes.AppOrDeviceName  => 4,
        DisplayModes.BarPercent       => 5,
        _                             => 0   // empty or unrecognised → "Use global default"
    };


    // Gets the dashboard mode constant from a ComboBox index.
    private static string GetChannelOledModeFromIndex(int index) => index switch
    {
        1 => DisplayModes.AppNameAndVolume,
        2 => DisplayModes.LargeVolume,
        3 => DisplayModes.MuteStatus,
        4 => DisplayModes.AppOrDeviceName,
        5 => DisplayModes.BarPercent,
        _ => string.Empty   // 0 or any other = use global default
    };


    private void SendChannelOledModeToDevice(int channelIndex)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived) return;
        if (channelIndex < 0 || channelIndex >= ChannelCount) return;

        string mode = GetChannelOledModeProtocolValue(_channels[channelIndex].OledDisplayMode);
        string message = $"{ProtocolCommands.DisplayMode},{channelIndex},{mode}";
        WriteSerialLine(message, logOutgoing: true);
    }


    private void SendAllChannelOledModesToDevice()
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived) return;

        for (int i = 0; i < ChannelCount; i++)
        {
            SendChannelOledModeToDevice(i);
        }
    }


    private void SendOledSettingsToDevice(bool logIfNotConnected)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            if (logIfNotConnected)
            {
                Log("OLED setup saved locally. Connect the controller to apply it to the physical OLED display.");
            }
            return;
        }

        string mode = GetOledDisplayModeProtocolValue();
        int brightness = GetOledBrightnessPercentFromUi();
        int disconnectedTimeout = GetOledSleepTimeoutMinutesFromUi();
        string connectedIdleAction = GetOledConnectedIdleActionProtocolValue();
        int connectedIdleTimeout = GetOledConnectedIdleTimeoutMinutesFromUi();
        int antiBurnIn = IsOledAntiBurnInEnabledFromUi() ? 1 : 0;
        string message = $"{ProtocolCommands.OledConfig},{mode},{brightness},{disconnectedTimeout},{connectedIdleAction},{connectedIdleTimeout},{antiBurnIn}";
        WriteSerialLine(message, logOutgoing: true);
        _lastStateSent = message;
        UpdateDiagnostics();
    }


    private void WriteSerialLine(string message, bool logOutgoing = false)
    {
        AppendDebugConsole("OUT", message);
        _serialService.SendLine(message);
        if (logOutgoing && _serialService.IsConnected)
            Log($"PC -> ESP32: {message}");
    }


    private void QueueFullStateSend(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 1);
        _fullStateSendCoalesceTimer?.Dispose();
        _fullStateSendCoalesceTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 0) == 1)
                    {
                        RefreshAllChannelStates();
                        SendAllChannelStatesToDevice();
                        SendStateToDevice(force: true);
                        if (IsAdvancedDebugLoggingEnabled())
                        {
                            Log($"Coalesced STATE update sent after {reason}.");
                        }
                    }
                });
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 0);
            }
        }, null, 180, System.Threading.Timeout.Infinite);
    }


    private static bool IsEspProtocolCompatible(string reportedProtocol)
    {
        if (string.IsNullOrWhiteSpace(reportedProtocol) || reportedProtocol.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CompareVersionParts(reportedProtocol, RequiredProtocolVersion) >= 0;
    }


    private static int CompareVersionParts(string left, string right)
    {
        int[] leftParts = ParseVersionParts(left);
        int[] rightParts = ParseVersionParts(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);

        for (int i = 0; i < count; i++)
        {
            int leftValue = i < leftParts.Length ? leftParts[i] : 0;
            int rightValue = i < rightParts.Length ? rightParts[i] : 0;

            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }


    private static int[] ParseVersionParts(string version)
    {
        string cleaned = version.Trim().TrimStart('v', 'V');
        StringBuilder builder = new();

        foreach (char c in cleaned)
        {
            builder.Append(char.IsDigit(c) ? c : '.');
        }

        return builder.ToString()
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out int value) ? value : 0)
            .ToArray();
    }

}
