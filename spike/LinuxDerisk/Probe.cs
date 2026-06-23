using System.Diagnostics;
using System.Globalization;
using PcVolumeControllerDashboard.Core;

namespace LinuxDerisk;

/// <summary>
/// Headless (no-GUI) version of the two derisk probes, so an agent or a plain
/// terminal can run the spike end-to-end and self-verify the results.
///
///   dotnet run --project spike/LinuxDerisk -- --headless [options]
///
/// Options:
///   --port &lt;name&gt;     serial port to open (default: first one found, e.g. /dev/ttyACM0)
///   --seconds &lt;n&gt;     how long to read serial before closing (default 8)
///   --node &lt;id&gt;       PipeWire node/sink-input id to set the volume on
///   --volume &lt;0-100&gt;  volume percent to set on --node (default 30)
///   --no-serial      skip the serial probe
///   --no-audio       skip the PipeWire probe
/// </summary>
internal static class Probe
{
    private const int BaudRate = 115200;

    public static int Run(string[] args)
    {
        Console.WriteLine("=== Phase 0.5 Linux derisk — headless probe ===");
        Console.WriteLine($"OS: {Environment.OSVersion}  |  .NET: {Environment.Version}");
        Console.WriteLine();

        if (!args.Contains("--no-serial"))
            SerialProbe(GetArg(args, "--port"), GetIntArg(args, "--seconds", 8));

        if (!args.Contains("--no-audio"))
            AudioProbe(GetArg(args, "--node"), GetIntArg(args, "--volume", 30));

        Console.WriteLine();
        Console.WriteLine("=== probe done — see PASS/FAIL lines above ===");
        return 0;
    }

    // ── serial ──────────────────────────────────────────────────────────────────────

    private static void SerialProbe(string? port, int seconds)
    {
        Console.WriteLine("--- [1] Serial probe (Core.SerialService) ---");

        string[] ports = SerialService.GetPortNames();
        Console.WriteLine($"Ports found: {(ports.Length == 0 ? "(none)" : string.Join(", ", ports))}");

        port ??= ports.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(port))
        {
            Console.WriteLine("FAIL serial: no port to open (is the ESP32 plugged in?).");
            Console.WriteLine();
            return;
        }

        int received = 0;
        bool sawHello = false;
        string? error = null;

        using var svc = new SerialService();
        svc.LineReceived += line =>
        {
            Interlocked.Increment(ref received);
            if (line.StartsWith("HELLO", StringComparison.OrdinalIgnoreCase)) sawHello = true;
            Console.WriteLine($"  RX  {line}");
        };
        svc.ErrorOccurred += msg => { error = msg; Console.WriteLine($"  ERR {msg}"); };

        try
        {
            svc.Open(port, BaudRate);
            Console.WriteLine($"Opened {port} @ {BaudRate}. Reading for {seconds}s…");
        }
        catch (Exception ex)
        {
            // Permission denied here => add your user to the serial group and re-login:
            //   Arch / CachyOS:        sudo usermod -aG uucp $USER
            //   Debian/Ubuntu/Fedora:  sudo usermod -aG dialout $USER
            Console.WriteLine($"FAIL serial: OPEN {port} threw {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("     (permission denied? add your user to 'uucp' on Arch/CachyOS, then re-login)");
            Console.WriteLine();
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        svc.Close();

        if (error != null && received == 0)
            Console.WriteLine($"FAIL serial: port opened but a read error occurred and no lines arrived.");
        else if (received == 0)
            Console.WriteLine("WARN serial: port opened OK but no lines received (device idle/asleep? still proves open+read path).");
        else
            Console.WriteLine($"PASS serial: opened {port} and received {received} line(s){(sawHello ? ", including HELLO" : "")}.");
        Console.WriteLine();
    }

    // ── pipewire ────────────────────────────────────────────────────────────────────

    private static void AudioProbe(string? node, int volumePercent)
    {
        Console.WriteLine("--- [2] PipeWire probe (wpctl) ---");

        var (ok, statusOut, statusErr) = Run("wpctl", "status");
        if (!ok)
        {
            Console.WriteLine($"wpctl unavailable ({statusErr.Trim()}). Falling back to pactl…");
            var (ok2, pout, perr) = Run("pactl", "list sink-inputs short");
            Console.WriteLine(ok2 ? pout : $"FAIL audio: neither wpctl nor pactl worked ({perr.Trim()}).");
            Console.WriteLine();
            return;
        }
        Console.WriteLine(statusOut);

        if (string.IsNullOrWhiteSpace(node))
        {
            Console.WriteLine("INFO audio: pass --node <id> (a stream id from the 'Streams' section above)");
            Console.WriteLine("     plus --volume <0-100> to actually set and verify a per-app volume.");
            Console.WriteLine();
            return;
        }

        string scalar = (Math.Clamp(volumePercent, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        var (setOk, _, setErr) = Run("wpctl", $"set-volume {node} {scalar}");
        if (!setOk)
        {
            Console.WriteLine($"FAIL audio: wpctl set-volume {node} {scalar} → {setErr.Trim()}");
            Console.WriteLine();
            return;
        }

        // Read back to prove it actually took effect.
        var (getOk, getOut, getErr) = Run("wpctl", $"get-volume {node}");
        if (getOk)
            Console.WriteLine($"PASS audio: set node {node} to {scalar}; wpctl get-volume → {getOut.Trim()}");
        else
            Console.WriteLine($"WARN audio: set-volume succeeded but get-volume failed ({getErr.Trim()}); check the mixer visually.");
        Console.WriteLine();
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

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    private static int GetIntArg(string[] args, string name, int fallback)
        => int.TryParse(GetArg(args, name), out int v) ? v : fallback;
}
