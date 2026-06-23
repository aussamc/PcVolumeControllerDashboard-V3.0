using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;

namespace LinuxDerisk;

/// <summary>
/// Throwaway derisk window. Two probes:
///   1. Serial — uses the real Core SerialService to open the ESP32 CDC port
///      (/dev/ttyACM0 etc.) at 115200 and stream received lines into the log.
///   2. PipeWire — shells out to `wpctl` to list audio nodes and set a
///      sink-input (per-app) volume.
/// Everything writes to the shared log (and stdout) so the answers are visible.
/// </summary>
internal sealed class MainWindow : Window
{
    private const int BaudRate = 115200; // matches the host dashboard

    private readonly ComboBox _portBox = new() { MinWidth = 220 };
    private readonly Button _connectButton = new() { Content = "Connect" };
    private readonly TextBox _nodeIdBox = new() { Watermark = "node id (from list)", MinWidth = 160 };
    private readonly Slider _volumeSlider = new() { Minimum = 0, Maximum = 100, Value = 30, Width = 240 };
    private readonly TextBlock _volumeLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _log = new()
    {
        AcceptsReturn = true,
        IsReadOnly = true,
        MinHeight = 240,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private SerialService? _serial;

    public MainWindow()
    {
        Title = "Phase 0.5 — Linux derisk spike";
        Width = 760;
        Height = 620;

        _volumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Value")
                UpdateVolumeLabel();
        };
        UpdateVolumeLabel();

        Content = BuildLayout();
        RefreshPorts();
        _connectButton.Click += (_, _) => ToggleConnection();
    }

    // ── layout ────────────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var refreshButton = new Button { Content = "Refresh ports" };
        refreshButton.Click += (_, _) => RefreshPorts();

        var serialRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "Serial:", VerticalAlignment = VerticalAlignment.Center }, _portBox, refreshButton, _connectButton },
        };

        var listNodesButton = new Button { Content = "List audio apps (wpctl status)" };
        listNodesButton.Click += (_, _) => ListAudioNodes();

        var setVolButton = new Button { Content = "Set volume" };
        setVolButton.Click += (_, _) => SetNodeVolume();

        var pipewireRow1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "PipeWire:", VerticalAlignment = VerticalAlignment.Center }, listNodesButton },
        };

        var pipewireRow2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _nodeIdBox, _volumeSlider, _volumeLabel, setVolButton },
        };

        var root = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "1) Serial probe — opens the ESP32 via Core.SerialService", FontWeight = Avalonia.Media.FontWeight.Bold },
                serialRow,
                new Separator(),
                new TextBlock { Text = "2) PipeWire probe — change a per-app volume via wpctl", FontWeight = Avalonia.Media.FontWeight.Bold },
                pipewireRow1,
                pipewireRow2,
                new Separator(),
                new TextBlock { Text = "Log", FontWeight = Avalonia.Media.FontWeight.Bold },
                new ScrollViewer { Content = _log, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            },
        };

        return root;
    }

    private void UpdateVolumeLabel() => _volumeLabel.Text = $"{(int)_volumeSlider.Value}%";

    // ── serial probe ────────────────────────────────────────────────────────────────

    private void RefreshPorts()
    {
        string[] ports = SerialService.GetPortNames();
        _portBox.ItemsSource = ports;
        if (ports.Length > 0 && _portBox.SelectedIndex < 0)
            _portBox.SelectedIndex = 0;
        Log($"Ports: {(ports.Length == 0 ? "(none found)" : string.Join(", ", ports))}");
    }

    private void ToggleConnection()
    {
        if (_serial is { IsConnected: true })
        {
            _serial.Close();
            _serial.Dispose();
            _serial = null;
            _connectButton.Content = "Connect";
            Log("Disconnected.");
            return;
        }

        if (_portBox.SelectedItem is not string port || string.IsNullOrWhiteSpace(port))
        {
            Log("No port selected.");
            return;
        }

        try
        {
            _serial = new SerialService();
            _serial.LineReceived += line => Dispatcher.UIThread.Post(() => Log($"RX  {line}"));
            _serial.ErrorOccurred += msg => Dispatcher.UIThread.Post(() => Log($"ERR {msg}"));
            _serial.Open(port, BaudRate);
            _connectButton.Content = "Disconnect";
            Log($"Opened {port} @ {BaudRate}. Waiting for HELLO… (expect lines from the ESP32)");
        }
        catch (Exception ex)
        {
            // The classic Linux failure here is permission denied — add your user
            // to the serial device's group and re-login:
            //   Arch / CachyOS:        sudo usermod -aG uucp $USER
            //   Debian/Ubuntu/Fedora:  sudo usermod -aG dialout $USER
            Log($"OPEN FAILED: {ex.GetType().Name}: {ex.Message}");
            _serial?.Dispose();
            _serial = null;
        }
    }

    // ── pipewire probe ──────────────────────────────────────────────────────────────

    private void ListAudioNodes()
    {
        // `wpctl status` shows the node tree; the Streams/sink-input ids are what
        // you paste into the node-id box. `pactl list sink-inputs short` is an
        // alternative if you prefer Pulse ids.
        var (ok, stdout, stderr) = Run("wpctl", "status");
        if (!ok)
        {
            Log($"wpctl not available ({stderr.Trim()}). Try: pactl list sink-inputs short");
            var (ok2, out2, err2) = Run("pactl", "list sink-inputs short");
            Log(ok2 ? out2 : $"pactl failed: {err2.Trim()}");
            return;
        }
        Log(stdout);
    }

    private void SetNodeVolume()
    {
        string id = _nodeIdBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            Log("Enter a node id first (use 'List audio apps').");
            return;
        }

        // wpctl takes a 0.0–1.0 scalar.
        string scalar = ((int)_volumeSlider.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        var (ok, stdout, stderr) = Run("wpctl", $"set-volume {id} {scalar}");
        Log(ok
            ? $"wpctl set-volume {id} {scalar} → OK {stdout}".TrimEnd()
            : $"wpctl set-volume failed: {stderr.Trim()}");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private static (bool ok, string stdout, string stderr) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return (false, "", "process did not start");
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode == 0, so, se);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        _log.Text += line + Environment.NewLine;
        _log.CaretIndex = _log.Text?.Length ?? 0;
    }
}
