using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using PcVolumeControllerDashboard.Core.Audio;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Tests for <see cref="AudioWriteQueue"/> — the background write worker that
/// keeps slow device drivers (network speakers, Bluetooth) from stalling the
/// encoder/OLED/overlay pipeline. The fake backend can block its write calls on
/// demand, letting each test deterministically pile up writes behind a "slow
/// device" and observe the coalescing.
/// </summary>
public class AudioWriteQueueTests : IDisposable
{
    private const int WaitMs = 5000;

    private readonly FakeAudioBackend _shared = new();
    private readonly FakeAudioBackend _writeBackend = new();
    private AudioWriteQueue? _queue;

    private AudioWriteQueue CreateQueue(bool dedicatedWriteBackend = true) =>
        _queue = new AudioWriteQueue(
            _shared,
            () => dedicatedWriteBackend ? _writeBackend : null);

    public void Dispose()
    {
        _writeBackend.ReleaseWrites();
        _queue?.Dispose();
    }

    // ── Basic flow ─────────────────────────────────────────────────────────────

    [Fact]
    public void AdjustVolume_applies_on_the_write_backend_and_predicts_the_result()
    {
        _shared.Volume = 0.30f;
        AudioWriteQueue queue = CreateQueue();

        int predicted = queue.AdjustVolume("PROC:chrome", +2, 0, 100);

        predicted.Should().Be(32);
        queue.WaitForDrain(WaitMs).Should().BeTrue();
        _writeBackend.AdjustCalls.Should().ContainSingle()
            .Which.Should().Be(("PROC:chrome", 2, 0, 100));
        _shared.AdjustCalls.Should().BeEmpty("writes must not touch the shared backend when a dedicated one exists");
    }

    [Fact]
    public void Null_factory_routes_writes_through_the_shared_backend()
    {
        _shared.Volume = 0.30f;
        AudioWriteQueue queue = CreateQueue(dedicatedWriteBackend: false);

        queue.AdjustVolume("PROC:chrome", +2, 0, 100);

        queue.WaitForDrain(WaitMs).Should().BeTrue();
        _shared.AdjustCalls.Should().ContainSingle();
        _writeBackend.AdjustCalls.Should().BeEmpty();
    }

    [Fact]
    public void AdjustVolume_returns_minus_one_and_queues_nothing_when_the_key_has_no_target()
    {
        _shared.Volume = -1f; // no live session
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("PROC:ghost", +2, 0, 100).Should().Be(-1);

        queue.WaitForDrain(WaitMs).Should().BeTrue();
        _writeBackend.AdjustCalls.Should().BeEmpty();
    }

    // ── Coalescing against a slow device ──────────────────────────────────────

    [Fact]
    public void Deltas_sum_into_one_catchup_write_while_a_write_is_in_flight()
    {
        _shared.Volume = 0.30f;
        _writeBackend.BlockWrites = true;
        AudioWriteQueue queue = CreateQueue();

        // First delta starts a (blocked) write on the worker.
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(32);
        _writeBackend.WaitForWriteStarted(WaitMs).Should().BeTrue();

        // Four more detents land while the device is busy: predictions advance
        // instantly, deltas coalesce.
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(34);
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(36);
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(38);
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(40);

        _writeBackend.ReleaseWrites();
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        _writeBackend.AdjustCalls.Should().HaveCount(2);
        _writeBackend.AdjustCalls[0].deltaPercent.Should().Be(2);
        _writeBackend.AdjustCalls[1].deltaPercent.Should().Be(8, "the four blocked detents must arrive as one summed write");
    }

    [Fact]
    public void Absolute_sets_are_latest_value_wins()
    {
        AudioWriteQueue queue = CreateQueue();
        _writeBackend.BlockWrites = true;

        queue.SetVolume("PROC:chrome", 0.10f);
        _writeBackend.WaitForWriteStarted(WaitMs).Should().BeTrue();
        queue.SetVolume("PROC:chrome", 0.20f);
        queue.SetVolume("PROC:chrome", 0.30f);

        _writeBackend.ReleaseWrites();
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        _writeBackend.SetVolumeCalls.Should().HaveCount(2, "the two queued sets must collapse to the latest value");
        _writeBackend.SetVolumeCalls.Last().volume.Should().BeApproximately(0.30f, 0.001f);
    }

    [Fact]
    public void An_absolute_set_supersedes_a_buffered_delta()
    {
        _shared.Volume = 0.30f;
        _writeBackend.BlockWrites = true;
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("PROC:chrome", +2, 0, 100); // starts the (blocked) write
        _writeBackend.WaitForWriteStarted(WaitMs).Should().BeTrue();
        queue.AdjustVolume("PROC:chrome", +2, 0, 100); // buffers a delta
        queue.SetVolume("PROC:chrome", 0.50f);         // absolute replaces it

        _writeBackend.ReleaseWrites();
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        _writeBackend.AdjustCalls.Should().ContainSingle("the buffered delta must be dropped in favour of the absolute set");
        _writeBackend.SetVolumeCalls.Should().ContainSingle()
            .Which.volume.Should().BeApproximately(0.50f, 0.001f);
    }

    // ── Prediction lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void Prediction_is_dropped_after_the_key_drains_and_reseeds_from_the_device()
    {
        _shared.Volume = 0.30f;
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(32);
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        queue.TryGetPredictedVolume("PROC:chrome").Should().BeNull("a drained key must not keep a stale prediction");

        _shared.Volume = 0.50f; // device changed (e.g. another app moved the volume)
        queue.AdjustVolume("PROC:chrome", +2, 0, 100).Should().Be(52, "the next op must re-seed from the fresh device value");
    }

    [Fact]
    public void Prediction_respects_the_channel_min_max_limits()
    {
        _shared.Volume = 0.98f;
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("PROC:chrome", +5, 10, 90).Should().Be(90);
        queue.AdjustVolume("PROC:chrome", +5, 10, 90).Should().Be(90);
    }

    // ── Mute ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Rapid_mute_toggles_alternate_against_a_blocked_device()
    {
        _shared.Muted = false;
        _writeBackend.BlockWrites = true;
        AudioWriteQueue queue = CreateQueue();

        queue.ToggleMute("PROC:chrome").Should().Be(true);
        _writeBackend.WaitForWriteStarted(WaitMs).Should().BeTrue();
        queue.ToggleMute("PROC:chrome").Should().Be(false, "the second toggle must see the still-pending first value");
        queue.ToggleMute("PROC:chrome").Should().Be(true);

        _writeBackend.ReleaseWrites();
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        _writeBackend.SetMuteCalls.Last().mute.Should().Be(true);
    }

    [Fact]
    public void ToggleMute_returns_null_when_the_key_has_no_target()
    {
        _shared.Muted = null;
        AudioWriteQueue queue = CreateQueue();

        queue.ToggleMute("PROC:ghost").Should().BeNull();
        queue.WaitForDrain(WaitMs).Should().BeTrue();
        _writeBackend.SetMuteCalls.Should().BeEmpty();
    }

    [Fact]
    public void GetMute_prefers_the_pending_value_over_the_device()
    {
        _shared.Muted = false;
        _writeBackend.BlockWrites = true;
        AudioWriteQueue queue = CreateQueue();

        queue.SetMute("PROC:chrome", true);
        queue.GetMute("PROC:chrome").Should().Be(true);

        _writeBackend.ReleaseWrites();
        queue.WaitForDrain(WaitMs).Should().BeTrue();
        _shared.Muted = true; // device now reflects the write
        queue.GetMute("PROC:chrome").Should().Be(true);
    }

    // ── Backend lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public void The_dedicated_write_backend_is_initialised_once_and_disposed_on_queue_dispose()
    {
        AudioWriteQueue queue = CreateQueue();

        queue.SetVolume("MASTER", 0.5f);
        queue.SetVolume("MASTER", 0.6f);
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        _writeBackend.InitialiseCount.Should().Be(1);

        queue.Dispose();
        _writeBackend.Disposed.Should().BeTrue();
    }

    [Fact]
    public void ResetWriteBackend_rebuilds_the_dedicated_instance_on_the_next_write()
    {
        var replacements = new List<FakeAudioBackend>();
        _queue = new AudioWriteQueue(_shared, () =>
        {
            var b = new FakeAudioBackend();
            replacements.Add(b);
            return b;
        });

        _queue.SetVolume("MASTER", 0.5f);
        _queue.WaitForDrain(WaitMs).Should().BeTrue();
        replacements.Should().HaveCount(1);

        _queue.ResetWriteBackend();
        _queue.SetVolume("MASTER", 0.6f);
        _queue.WaitForDrain(WaitMs).Should().BeTrue();

        replacements.Should().HaveCount(2, "the reset must discard the old instance and build a fresh one");
        replacements[0].Disposed.Should().BeTrue();
        replacements[1].SetVolumeCalls.Should().ContainSingle();
    }

    // ── Fake backend ───────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IAudioBackend"/> fake: reads served from settable fields,
    /// writes recorded, with an optional gate that blocks write calls until
    /// released (simulating a slow device driver).
    /// </summary>
    // ── Stale-read backends (PipeWire) ─────────────────────────────────────────

    /// <summary>
    /// Regression: on a backend whose reads lag its writes, consecutive detents must
    /// keep building on the prediction rather than re-seeding from a pre-write read.
    ///
    /// Reproduces the reported "fast turns are erratic" bug on Linux. The queue used
    /// to drop its prediction the instant a key drained, so the next detent seeded
    /// from pw-dump's snapshot — up to 300 ms stale, roughly 8 detents at the
    /// measured turn rate — and the volume visibly jumped backwards mid-turn.
    /// </summary>
    [Fact]
    public void Consecutive_adjusts_do_not_re_seed_from_a_stale_read_while_within_the_staleness_window()
    {
        _shared.StalenessMs = 300;
        _writeBackend.StalenessMs = 300;
        _shared.Volume = 0.50f;   // the snapshot is frozen here: it never observes our writes
        AudioWriteQueue queue = CreateQueue();

        int first = queue.AdjustVolume("MASTER", 6, 0, 100);
        first.Should().Be(56);

        // Let the write fully drain, exactly as it would between two fast detents.
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        // The stale read still says 50 %. Without the hold window this returns 56
        // again (50 + 6) — the backwards jump the user sees.
        int second = queue.AdjustVolume("MASTER", 6, 0, 100);
        second.Should().Be(62, "the second detent must build on the prediction, not the stale read");

        queue.WaitForDrain(WaitMs).Should().BeTrue();
        queue.AdjustVolume("MASTER", 6, 0, 100).Should().Be(68);
    }

    /// <summary>
    /// The hold is bounded: once the staleness window passes, reads take over again
    /// so an externally-changed volume (another app, the system mixer) is picked up.
    /// </summary>
    [Fact]
    public void Prediction_is_dropped_once_the_staleness_window_expires()
    {
        _shared.StalenessMs = 40;
        _writeBackend.StalenessMs = 40;
        _shared.Volume = 0.50f;
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("MASTER", 6, 0, 100).Should().Be(56);
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        // Something else moved the volume while we were idle.
        _shared.Volume = 0.20f;
        Thread.Sleep(120);   // comfortably past the 40 ms window

        queue.AdjustVolume("MASTER", 6, 0, 100).Should().Be(26, "an expired prediction must defer to the device");
    }

    /// <summary>
    /// A backend reporting no staleness (WASAPI: reads are authoritative the moment a
    /// write returns) keeps the original drop-on-drain behaviour, so Windows is
    /// unaffected by the hold window.
    /// </summary>
    [Fact]
    public void Zero_staleness_backend_re_seeds_from_the_device_immediately()
    {
        _shared.StalenessMs = 0;
        _writeBackend.StalenessMs = 0;
        _shared.Volume = 0.50f;
        AudioWriteQueue queue = CreateQueue();

        queue.AdjustVolume("MASTER", 6, 0, 100).Should().Be(56);
        queue.WaitForDrain(WaitMs).Should().BeTrue();

        // Read still reports 50 %; with no staleness declared the queue trusts it.
        queue.AdjustVolume("MASTER", 6, 0, 100).Should().Be(56);
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public float Volume = 0.5f;
        public bool? Muted = false;
        public volatile bool BlockWrites;
        /// <summary>Simulates a backend whose reads lag its writes (PipeWire's pw-dump snapshot).</summary>
        public int StalenessMs;
        public bool Disposed;
        public int InitialiseCount;

        public List<(string key, int deltaPercent, int minPercent, int maxPercent)> AdjustCalls { get; } = new();
        public List<(string key, float volume)> SetVolumeCalls { get; } = new();
        public List<(string key, bool mute)> SetMuteCalls { get; } = new();

        private readonly ManualResetEventSlim _writeStarted = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public bool WaitForWriteStarted(int timeoutMs) => _writeStarted.Wait(timeoutMs);

        public void ReleaseWrites()
        {
            BlockWrites = false;
            _release.Set();
        }

        private void EnterWrite()
        {
            _writeStarted.Set();
            if (BlockWrites) _release.Wait();
        }

        public string BackendName => "Fake";
        public int ReadStalenessMs => StalenessMs;
        public bool IsAvailable => true;
        public event Action? AvailabilityChanged { add { } remove { } }
        public event Action? TargetsChanged { add { } remove { } }

        public void Initialise() => Interlocked.Increment(ref InitialiseCount);
        public IReadOnlyList<AudioTarget> GetAvailableTargets() => Array.Empty<AudioTarget>();
        public float GetVolumeByKey(string key) => Volume;
        public bool IsKeyActive(string key) => Volume >= 0f;

        public bool SetVolumeByKey(string key, float normalizedVolume)
        {
            EnterWrite();
            lock (SetVolumeCalls) SetVolumeCalls.Add((key, normalizedVolume));
            return true;
        }

        public int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent)
        {
            EnterWrite();
            lock (AdjustCalls) AdjustCalls.Add((key, deltaPercent, minPercent, maxPercent));
            return Math.Clamp((int)Math.Round(Volume * 100) + deltaPercent, minPercent, maxPercent);
        }

        public bool? GetMuteByKey(string key) => Muted;

        public bool SetMuteByKey(string key, bool mute)
        {
            EnterWrite();
            lock (SetMuteCalls) SetMuteCalls.Add((key, mute));
            Muted = mute;
            return true;
        }

        public bool? ToggleMuteByKey(string key)
        {
            EnterWrite();
            if (Muted == null) return null;
            Muted = !Muted.Value;
            return Muted;
        }

        public void InvalidateCache() { }
        public void Dispose() => Disposed = true;
    }
}
