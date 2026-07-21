using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.Platform.Linux;

/// <summary>
/// Linux audio backend for PipeWire (via its PulseAudio-compatible graph, driven
/// through WirePlumber's <c>wpctl</c> CLI). Implements the neutral
/// <see cref="IAudioBackend"/> for the default sink (master) and source (mic)
/// plus per-application stream nodes.
///
/// All reads come from a single periodically-refreshed <c>pw-dump</c> JSON graph
/// snapshot (id, media class, app label, run state, volume, mute for every node) —
/// no shell-out happens on the read path. Only mutations (<c>wpctl set-volume</c>/
/// <c>set-mute</c>) shell out synchronously, since they're driven by discrete user
/// actions (an encoder turn or button press), not the UI's 20Hz poll.
///
/// Keys: <c>MASTER</c> (resolved via the graph's default-sink metadata),
/// <c>MIC_INPUT</c> (default-source metadata), <c>PROC:&lt;appLabel&gt;</c> (one or
/// more <c>Stream/Output/Audio</c> nodes sharing that label — e.g. two browser
/// tabs); volume/mute operations apply to every matching node.
/// </summary>
public sealed class PipeWireAudioBackend : IAudioBackend
{
    private const string StreamMediaClass = "Stream/Output/Audio";
    private const int RefreshIntervalMs = 150;

    private sealed record NodeInfo(int Id, string MediaClass, string Label, string NodeName, string State, float Volume, bool Muted);

    private sealed record Snapshot(IReadOnlyList<NodeInfo> Nodes, string? DefaultSinkName, string? DefaultSourceName)
    {
        public static readonly Snapshot Empty = new(Array.Empty<NodeInfo>(), null, null);
    }

    private readonly Action<string> _log;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private volatile bool _isAvailable;
    private Timer? _timer;
    private int _refreshing;
    private HashSet<string> _lastKeys = new(StringComparer.OrdinalIgnoreCase);

    public string BackendName => "PipeWire";

    /// <summary>
    /// Reads come from the <c>pw-dump</c> snapshot, so a <c>wpctl set-volume</c> that
    /// has already returned isn't visible here until the next refresh lands. Budget
    /// two intervals: one for the refresh that may have started just before the write,
    /// one for the refresh that actually observes it, plus the <c>pw-dump</c> exec.
    ///
    /// This must not be under-reported. A fast encoder turn produces detents every
    /// ~10–20 ms; if <see cref="AudioWriteQueue"/> stops trusting its prediction
    /// before the snapshot catches up, the next detent re-seeds from a value up to a
    /// full interval old and the volume jumps backwards mid-turn.
    /// </summary>
    public int ReadStalenessMs => RefreshIntervalMs * 2;

    public bool IsAvailable => _isAvailable;

    public event Action? AvailabilityChanged;

    public event Action? TargetsChanged;

    /// <param name="logger">Optional diagnostics delegate; may be invoked on any thread.</param>
    public PipeWireAudioBackend(Action<string>? logger = null)
    {
        _log = logger ?? (_ => { });
    }

    // ─────────────────────────────────────────── init ──

    public void Initialise()
    {
        RefreshSnapshot();
        _timer = new Timer(_ => RefreshSnapshot(), null, RefreshIntervalMs, RefreshIntervalMs);
    }

    public void InvalidateCache() => RefreshSnapshot();

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // ─────────────────────────────────────────── snapshot refresh ──

    private void RefreshSnapshot()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            (bool ok, string stdout, string stderr) = Run("pw-dump");
            if (!ok)
            {
                _log($"PipeWireAudioBackend: pw-dump unavailable: {stderr.Trim()}");
                SetAvailability(false);
                return;
            }

            Snapshot next;
            try
            {
                next = Parse(stdout);
            }
            catch (Exception ex)
            {
                _log($"PipeWireAudioBackend: pw-dump parse failed: {ex.Message}");
                SetAvailability(false);
                return;
            }

            _snapshot = next;
            SetAvailability(true);

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MASTER", "MIC_INPUT" };
            foreach (NodeInfo n in next.Nodes)
                if (n.MediaClass == StreamMediaClass)
                    keys.Add($"PROC:{n.Label}");

            if (!keys.SetEquals(_lastKeys))
            {
                _lastKeys = keys;
                try { TargetsChanged?.Invoke(); } catch { }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void SetAvailability(bool available)
    {
        if (_isAvailable == available) return;
        _isAvailable = available;
        try { AvailabilityChanged?.Invoke(); } catch { }
    }

    private static Snapshot Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var nodes = new List<NodeInfo>();
        string? defaultSink = null;
        string? defaultSource = null;

        foreach (JsonElement obj in doc.RootElement.EnumerateArray())
        {
            string? type = GetString(obj, "type");

            if (type == "PipeWire:Interface:Metadata")
            {
                if (!obj.TryGetProperty("metadata", out JsonElement metaArr) || metaArr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement m in metaArr.EnumerateArray())
                {
                    string? key = GetString(m, "key");
                    if (key != "default.audio.sink" && key != "default.audio.source") continue;
                    if (!m.TryGetProperty("value", out JsonElement vEl)) continue;
                    string? name = GetString(vEl, "name");
                    if (key == "default.audio.sink") defaultSink = name;
                    else defaultSource = name;
                }
                continue;
            }

            if (type != "PipeWire:Interface:Node") continue;
            if (!obj.TryGetProperty("id", out JsonElement idEl)) continue;
            if (!obj.TryGetProperty("info", out JsonElement info)) continue;

            string state = GetString(info, "state") ?? "";
            if (!info.TryGetProperty("props", out JsonElement props)) continue;

            string mediaClass = GetString(props, "media.class") ?? "";
            string nodeName = GetString(props, "node.name") ?? "";
            string label = GetString(props, "application.name")
                ?? GetString(props, "application.process.binary")
                ?? nodeName;

            (float volume, bool muted) = ReadVolumeAndMute(info);

            nodes.Add(new NodeInfo(idEl.GetInt32(), mediaClass, label, nodeName, state, volume, muted));
        }

        return new Snapshot(nodes, defaultSink, defaultSource);
    }

    private static (float volume, bool muted) ReadVolumeAndMute(JsonElement info)
    {
        if (info.TryGetProperty("params", out JsonElement paramsEl) &&
            paramsEl.TryGetProperty("Props", out JsonElement propsParam) &&
            propsParam.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement p in propsParam.EnumerateArray())
            {
                if (!p.TryGetProperty("mute", out JsonElement muteEl)) continue;
                return (ReadChannelVolume(p), muteEl.GetBoolean());
            }
        }

        return (1f, false);
    }

    /// <summary>
    /// Averages <c>channelVolumes</c> and takes the cube root. PipeWire stores
    /// channel volume as amplitude^3 for perceptual linearity; <c>wpctl</c> (both
    /// <c>get-volume</c>'s output and what <c>set-volume</c> accepts) and this
    /// class's own writes all work in that cube-root ("cubic volume") scale —
    /// confirmed against a live node on this machine (raw channelVolumes 0.00274
    /// corresponded to `wpctl get-volume` reporting 0.14; 0.14^3 ≈ 0.00274). Reading
    /// the raw linear value directly would make every target appear near 100%.
    /// </summary>
    private static float ReadChannelVolume(JsonElement props)
    {
        if (!props.TryGetProperty("channelVolumes", out JsonElement cv) || cv.ValueKind != JsonValueKind.Array)
            return 1f;

        double sum = 0;
        int count = 0;
        foreach (JsonElement c in cv.EnumerateArray())
        {
            sum += c.GetDouble();
            count++;
        }

        return count == 0 ? 1f : (float)Math.Cbrt(sum / count);
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    // ─────────────────────────────────────────── target resolution ──

    private static bool IsMasterKey(string key) => key == "MASTER";
    private static bool IsMicKey(string key) => key == "MIC_INPUT";

    private IEnumerable<NodeInfo> ResolveNodes(string key)
    {
        Snapshot snap = _snapshot;

        if (IsMasterKey(key))
        {
            NodeInfo? n = FindByNodeName(snap, snap.DefaultSinkName);
            if (n != null) yield return n;
            yield break;
        }

        if (IsMicKey(key))
        {
            NodeInfo? n = FindByNodeName(snap, snap.DefaultSourceName);
            if (n != null) yield return n;
            yield break;
        }

        if (!key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase)) yield break;
        string name = key["PROC:".Length..];
        foreach (NodeInfo n in snap.Nodes)
            if (n.MediaClass == StreamMediaClass && string.Equals(n.Label, name, StringComparison.OrdinalIgnoreCase))
                yield return n;
    }

    private static NodeInfo? FindByNodeName(Snapshot snap, string? nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return null;
        foreach (NodeInfo n in snap.Nodes)
            if (n.NodeName == nodeName) return n;
        return null;
    }

    private static int PercentOf(float normalized) => Math.Clamp((int)Math.Round(normalized * 100), 0, 100);

    // ─────────────────────────────────────────── IAudioBackend reads ──

    public IReadOnlyList<AudioTarget> GetAvailableTargets()
    {
        if (!_isAvailable) return Array.Empty<AudioTarget>();
        Snapshot snap = _snapshot;
        var targets = new List<AudioTarget>();

        AudioTarget master = AudioTarget.CreateMaster();
        NodeInfo? masterNode = FindByNodeName(snap, snap.DefaultSinkName);
        if (masterNode != null)
        {
            master.Volume = PercentOf(masterNode.Volume);
            master.Muted = masterNode.Muted;
            master.IsLive = true;
        }
        targets.Add(master);

        AudioTarget mic = AudioTarget.CreateMic();
        NodeInfo? micNode = FindByNodeName(snap, snap.DefaultSourceName);
        if (micNode != null)
        {
            mic.Volume = PercentOf(micNode.Volume);
            mic.Muted = micNode.Muted;
            mic.IsLive = true;
        }
        targets.Add(mic);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NodeInfo n in snap.Nodes)
        {
            if (n.MediaClass != StreamMediaClass) continue;
            if (!seen.Add(n.Label)) continue; // one row represents the whole PROC:<label> group

            targets.Add(new AudioTarget
            {
                Key = $"PROC:{n.Label}",
                Label = n.Label,
                ProcessName = n.Label,
                Volume = PercentOf(n.Volume),
                Muted = n.Muted,
                State = n.State == "running" ? "Active" : "Idle",
                IsLive = true,
            });
        }

        return targets;
    }

    public float GetVolumeByKey(string key)
    {
        foreach (NodeInfo n in ResolveNodes(key)) return n.Volume;
        return -1f;
    }

    public bool IsKeyActive(string key)
    {
        if (IsMasterKey(key) || IsMicKey(key)) return true;
        foreach (NodeInfo n in ResolveNodes(key))
            if (n.State == "running") return true;
        return false;
    }

    public bool? GetMuteByKey(string key)
    {
        foreach (NodeInfo n in ResolveNodes(key)) return n.Muted;
        return null;
    }

    // ─────────────────────────────────────────── IAudioBackend writes ──

    public bool SetVolumeByKey(string key, float normalizedVolume)
    {
        List<int> ids = ResolveNodes(key).Select(n => n.Id).ToList();
        if (ids.Count == 0) return false;

        string val = Math.Clamp(normalizedVolume, 0f, 1f).ToString("0.00", CultureInfo.InvariantCulture);
        bool any = false;
        foreach (int id in ids)
            if (Run("wpctl", "set-volume", id.ToString(CultureInfo.InvariantCulture), val).ok) any = true;

        if (any) RefreshSnapshot();
        return any;
    }

    public int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent)
    {
        List<int> ids = ResolveNodes(key).Select(n => n.Id).ToList();
        if (ids.Count == 0) return -1;

        string sign = deltaPercent >= 0 ? "+" : "-";
        string step = $"{Math.Abs(deltaPercent).ToString(CultureInfo.InvariantCulture)}%{sign}";
        foreach (int id in ids)
            Run("wpctl", "set-volume", id.ToString(CultureInfo.InvariantCulture), step);

        RefreshSnapshot();

        int representative = -1;
        foreach (int id in ids)
        {
            NodeInfo? n = _snapshot.Nodes.FirstOrDefault(x => x.Id == id);
            if (n == null) continue;

            int pct = PercentOf(n.Volume);
            int clamped = Math.Clamp(pct, minPercent, maxPercent);
            if (clamped != pct)
            {
                string abs = (clamped / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
                Run("wpctl", "set-volume", id.ToString(CultureInfo.InvariantCulture), abs);
                pct = clamped;
            }

            if (representative < 0) representative = pct;
        }

        if (representative >= 0) RefreshSnapshot();
        return representative;
    }

    public bool SetMuteByKey(string key, bool mute)
    {
        List<int> ids = ResolveNodes(key).Select(n => n.Id).ToList();
        if (ids.Count == 0) return false;

        string val = mute ? "1" : "0";
        bool any = false;
        foreach (int id in ids)
            if (Run("wpctl", "set-mute", id.ToString(CultureInfo.InvariantCulture), val).ok) any = true;

        if (any) RefreshSnapshot();
        return any;
    }

    public bool? ToggleMuteByKey(string key)
    {
        List<NodeInfo> nodes = ResolveNodes(key).ToList();
        if (nodes.Count == 0) return null;

        bool newState = !nodes[0].Muted;
        string val = newState ? "1" : "0";
        foreach (NodeInfo n in nodes)
            Run("wpctl", "set-mute", n.Id.ToString(CultureInfo.InvariantCulture), val);

        RefreshSnapshot();
        return newState;
    }

    // ─────────────────────────────────────────── process shell-out ──

    private static (bool ok, string stdout, string stderr) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process? p = Process.Start(psi);
            if (p == null) return (false, "", "process did not start");

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
