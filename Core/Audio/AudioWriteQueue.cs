namespace PcVolumeControllerDashboard.Core.Audio;

/// <summary>
/// Serialises audio <b>writes</b> onto a dedicated background thread with per-key
/// coalescing, so a slow device driver can never stall the caller.
///
/// Why: volume/mute writes are ordinary synchronous driver calls, and some
/// endpoints service them slowly — a per-session volume write on a network
/// speaker (e.g. Sonos) was measured at 250–550 ms. With writes applied inline on
/// the UI thread, a 20-detent encoder turn became 20 serialized slow writes: the
/// knob felt dead and the OLEDs/overlay trailed by many seconds. Reads are cheap
/// (session-cache backed), so they stay on the caller's thread; only writes queue.
///
/// Coalescing semantics per target key:
///   • Relative encoder deltas <b>sum</b> while a write is in flight, so a fast
///     turn against a slow device becomes one catch-up write with the summed
///     delta (preserving the per-session relative-adjust semantics of
///     <see cref="IAudioBackend.AdjustVolumeByKey"/>).
///   • Absolute volume sets are <b>latest-value-wins</b> and supersede any
///     buffered delta (the prediction they're based on already includes it).
///   • Mute sets are latest-value-wins, independent of volume.
///
/// Callers get instant feedback from a per-key <b>predicted</b> volume/mute —
/// seeded from a real read when a key goes busy, advanced locally per queued op,
/// and discarded once the key's writes drain <i>and</i>
/// <see cref="IAudioBackend.ReadStalenessMs"/> has elapsed (the next op then
/// re-seeds from the device, so any prediction drift self-corrects at the first
/// idle moment). The extra hold matters on backends whose reads lag their writes:
/// dropping the prediction the instant the queue empties re-seeds the following
/// detent from a pre-write value, which reads as the volume jumping backwards
/// part-way through a fast turn.
///
/// Thread model: the worker owns a <b>dedicated backend instance</b> from
/// <paramref name="writeBackendFactory"/> (created lazily on the worker thread,
/// so COM objects never cross threads). A factory returning null means "no
/// dedicated instance is needed or safe" (VoiceMeeter's Remote DLL allows one
/// login per process but is fast and thread-agnostic) and the worker uses the
/// shared backend directly. <see cref="ResetWriteBackend"/> rebuilds the
/// dedicated instance after a backend-mode switch.
/// </summary>
public sealed class AudioWriteQueue : IDisposable
{
    private sealed class PendingWrite
    {
        public bool HasAbsolute;
        public float AbsoluteVolume;
        public bool HasDelta;
        public int DeltaPercent;
        public int MinPercent;
        public int MaxPercent = 100;
        public bool? Mute;
    }

    private readonly IAudioBackend _shared;
    private readonly Func<IAudioBackend?> _writeBackendFactory;
    private readonly Action<string>? _log;
    private readonly Thread _worker;

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingWrite> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    // Predicted state per key while it has pending/in-flight writes, and for
    // IAudioBackend.ReadStalenessMs afterwards — see _predictionHoldUntil.
    private readonly Dictionary<string, int> _predictedPercent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _predictedMute = new(StringComparer.OrdinalIgnoreCase);

    // When a key's writes drain, its prediction is kept until this tick rather than
    // dropped immediately. On a backend whose reads lag writes (PipeWire serves reads
    // from a periodically-refreshed pw-dump snapshot), re-seeding the moment the queue
    // empties pulls a pre-write value: during a fast encoder turn the next detent then
    // lands on a base up to a full refresh interval stale and the volume jumps
    // backwards. Holding the prediction across that window keeps consecutive detents
    // building on each other. A key absent from this map has a live prediction.
    private readonly Dictionary<string, long> _predictionHoldUntil = new(StringComparer.OrdinalIgnoreCase);

    private string? _inFlightKey;
    private bool _recreateBackend;
    private bool _disposed;

    // Worker-thread-only state.
    private IAudioBackend? _ownBackend;
    private bool _ownBackendFailed;

    public AudioWriteQueue(IAudioBackend sharedBackend, Func<IAudioBackend?> writeBackendFactory, Action<string>? log = null)
    {
        _shared = sharedBackend;
        _writeBackendFactory = writeBackendFactory;
        _log = log;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AudioWriteQueue" };
        _worker.Start();
    }

    // ── Enqueue API (call from the thread that owns the shared backend) ────────

    /// <summary>
    /// Queues a relative volume change and returns the <b>predicted</b> resulting
    /// percentage for immediate UI feedback, or −1 when the key has no live
    /// target (nothing is queued then, matching
    /// <see cref="IAudioBackend.AdjustVolumeByKey"/>'s failure contract).
    /// </summary>
    public int AdjustVolume(string key, int deltaPercent, int minPercent, int maxPercent)
    {
        // Seed read outside the lock; cheap, and never contends with the worker.
        float deviceVolume = _shared.GetVolumeByKey(key);

        lock (_gate)
        {
            if (_disposed) return -1;

            ExpireStalePredictions(key);

            int basePercent = _predictedPercent.TryGetValue(key, out int predicted)
                ? predicted
                : deviceVolume < 0f ? -1 : (int)Math.Round(deviceVolume * 100);
            if (basePercent < 0) return -1;

            int next = Math.Clamp(basePercent + deltaPercent, minPercent, maxPercent);
            _predictedPercent[key] = next;

            PendingWrite op = GetOrEnqueuePending(key);
            op.HasDelta = true;
            op.DeltaPercent += deltaPercent;
            op.MinPercent = minPercent;
            op.MaxPercent = maxPercent;

            Monitor.PulseAll(_gate);
            return next;
        }
    }

    /// <summary>Queues an absolute volume set (latest value wins per key).</summary>
    public void SetVolume(string key, float normalizedVolume)
    {
        float v = Math.Clamp(normalizedVolume, 0f, 1f);
        lock (_gate)
        {
            if (_disposed) return;

            PendingWrite op = GetOrEnqueuePending(key);
            op.HasAbsolute = true;
            op.AbsoluteVolume = v;
            op.HasDelta = false;      // superseded: the absolute value replaces any buffered delta
            op.DeltaPercent = 0;
            _predictedPercent[key] = (int)Math.Round(v * 100);

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Queues a mute set (latest value wins per key).</summary>
    public void SetMute(string key, bool mute)
    {
        lock (_gate)
        {
            if (_disposed) return;

            PendingWrite op = GetOrEnqueuePending(key);
            op.Mute = mute;
            _predictedMute[key] = mute;

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Toggles mute using the pending state when one is queued (so rapid toggles
    /// alternate correctly even while a slow write is in flight), falling back to
    /// a device read. Returns the new state, or null when the key has no target.
    /// </summary>
    public bool? ToggleMute(string key)
    {
        bool? current;
        lock (_gate)
        {
            ExpireStalePredictions(key);
            current = _predictedMute.TryGetValue(key, out bool queued) ? queued : null;
        }
        current ??= _shared.GetMuteByKey(key);
        if (current == null) return null;

        bool next = !current.Value;
        SetMute(key, next);
        return next;
    }

    /// <summary>
    /// The predicted normalised volume for a key with pending/in-flight writes,
    /// or null when the key is idle (read the backend instead).
    /// </summary>
    public float? TryGetPredictedVolume(string key)
    {
        lock (_gate)
        {
            ExpireStalePredictions(key);
            return _predictedPercent.TryGetValue(key, out int percent) ? percent / 100f : null;
        }
    }

    /// <summary>
    /// Drops a drained key's prediction once its hold window has passed, so reads
    /// take over again and any drift self-corrects at the first idle moment. Called
    /// under <c>_gate</c> before every prediction lookup — lazy expiry, so no timer
    /// is needed and an idle key costs nothing.
    /// </summary>
    private void ExpireStalePredictions(string key)
    {
        if (!_predictionHoldUntil.TryGetValue(key, out long holdUntil)) return;
        if (Environment.TickCount64 < holdUntil) return;

        _predictionHoldUntil.Remove(key);
        _predictedPercent.Remove(key);
        _predictedMute.Remove(key);
    }

    /// <summary>
    /// Current mute state: the queued value when a mute write is pending/in
    /// flight, else a device read. Null when the key has no target.
    /// </summary>
    public bool? GetMute(string key)
    {
        lock (_gate)
        {
            ExpireStalePredictions(key);
            if (_predictedMute.TryGetValue(key, out bool queued)) return queued;
        }
        return _shared.GetMuteByKey(key);
    }

    /// <summary>
    /// Discards the dedicated write backend so the next write re-consults the
    /// factory. Call after the shared backend switches mode (WASAPI ↔ VoiceMeeter).
    /// </summary>
    public void ResetWriteBackend()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _recreateBackend = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Blocks until every queued write has been applied (or the timeout expires).
    /// Returns true when the queue drained. Intended for tests and shutdown.
    /// </summary>
    public bool WaitForDrain(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (_pending.Count > 0 || _inFlightKey != null)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, (int)Math.Min(remaining, 50));
            }
            return true;
        }
    }

    private PendingWrite GetOrEnqueuePending(string key)
    {
        // The key is busy again, so its prediction is live rather than winding down —
        // clear any hold started by a previous drain.
        _predictionHoldUntil.Remove(key);

        if (_pending.TryGetValue(key, out PendingWrite? op)) return op;

        op = new PendingWrite();
        _pending[key] = op;
        _order.Enqueue(key);
        return op;
    }

    // ── Worker ─────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        while (true)
        {
            string? key = null;
            PendingWrite? op = null;
            bool recreate = false;

            lock (_gate)
            {
                _inFlightKey = null;
                Monitor.PulseAll(_gate); // wake WaitForDrain

                while (!_disposed && !_recreateBackend && _order.Count == 0)
                    Monitor.Wait(_gate);

                if (_disposed) break;

                if (_recreateBackend)
                {
                    _recreateBackend = false;
                    recreate = true;
                }
                else
                {
                    key = _order.Dequeue();
                    if (!_pending.Remove(key, out op)) continue;
                    _inFlightKey = key;
                }
            }

            if (recreate)
            {
                DisposeOwnBackend();
                _ownBackendFailed = false;
                continue;
            }

            if (key == null || op == null) continue;

            IAudioBackend backend = EnsureWriteBackend();
            try
            {
                if (op.HasAbsolute)
                    backend.SetVolumeByKey(key, op.AbsoluteVolume);
                if (op.HasDelta && op.DeltaPercent != 0)
                    backend.AdjustVolumeByKey(key, op.DeltaPercent, op.MinPercent, op.MaxPercent);
                if (op.Mute is bool mute)
                    backend.SetMuteByKey(key, mute);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"AudioWriteQueue: write for {key} failed: {ex.Message}");
            }

            lock (_gate)
            {
                _inFlightKey = null;
                // The key is flushed, but the device may not *read back* as flushed
                // yet. Start the hold window instead of dropping the prediction now;
                // ExpireStalePredictions drops it once reads can be trusted again.
                if (!_pending.ContainsKey(key))
                {
                    int staleness = Math.Max(0, backend.ReadStalenessMs);
                    if (staleness == 0)
                    {
                        _predictedPercent.Remove(key);
                        _predictedMute.Remove(key);
                        _predictionHoldUntil.Remove(key);
                    }
                    else
                    {
                        _predictionHoldUntil[key] = Environment.TickCount64 + staleness;
                    }
                }
                Monitor.PulseAll(_gate);
            }
        }

        DisposeOwnBackend();
    }

    /// <summary>
    /// The backend this worker should write through: the dedicated instance when
    /// the factory provides one (created lazily here, on the worker thread), else
    /// the shared backend.
    /// </summary>
    private IAudioBackend EnsureWriteBackend()
    {
        if (_ownBackend != null) return _ownBackend;
        if (_ownBackendFailed) return _shared;

        try
        {
            IAudioBackend? created = _writeBackendFactory();
            if (created == null)
            {
                _ownBackendFailed = true; // by design: use the shared backend
                return _shared;
            }
            created.Initialise();
            _ownBackend = created;
            return created;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AudioWriteQueue: dedicated write backend failed to start ({ex.Message}); writing via the shared backend.");
            _ownBackendFailed = true;
            return _shared;
        }
    }

    private void DisposeOwnBackend()
    {
        try { _ownBackend?.Dispose(); } catch { }
        _ownBackend = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
        if (!_worker.Join(2000))
            _log?.Invoke("AudioWriteQueue: worker did not stop within 2 s (background thread; will die with the process).");
    }
}
