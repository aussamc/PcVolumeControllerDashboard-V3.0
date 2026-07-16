using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App.Audio;

/// <summary>
/// Resolves a channel's effective audio target, supporting multi-app pools — an
/// ordered list of target keys where the first one with a live session wins (so a
/// "browser" channel can follow whichever browser/media app is currently playing).
/// A single <see cref="ChannelSettings.TargetKey"/> is the degenerate case.
/// </summary>
public static class ChannelTargets
{
    /// <summary>
    /// The key the channel should currently control. With a non-empty pool, returns
    /// the first pool entry that has a live session; if none are live, the first
    /// non-empty entry (so the encoder still targets something). Otherwise the
    /// single <see cref="ChannelSettings.TargetKey"/>.
    /// </summary>
    public static string ResolveActiveKey(ChannelSettings ch, IAudioBackend audio)
    {
        var pool = ch.TargetKeys;
        if (pool == null || pool.Count == 0)
            return ch.TargetKey;

        string? firstNonEmpty = null;
        string? firstAvailable = null;
        foreach (string k in pool)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            firstNonEmpty ??= k;

            bool available = audio.GetVolumeByKey(k) >= 0f;
            if (available)
            {
                firstAvailable ??= k;
                if (audio.IsKeyActive(k)) return k; // the one currently making sound wins
            }
        }

        // Nothing actively playing → fall back to the first running app, else the
        // first entry, so the knob still targets something meaningful.
        return firstAvailable ?? firstNonEmpty ?? ch.TargetKey;
    }

    /// <summary>True if the channel has any assigned target (single or a non-empty pool).</summary>
    public static bool HasTarget(ChannelSettings ch) =>
        !string.IsNullOrWhiteSpace(ch.TargetKey) ||
        (ch.TargetKeys != null && ch.TargetKeys.Exists(k => !string.IsNullOrWhiteSpace(k)));

    /// <summary>
    /// True only when the channel is a genuine multi-app pool — i.e. it has <b>two or
    /// more</b> non-empty target keys. A single entry is just a plain single target
    /// (the pool degenerates to <see cref="ChannelSettings.TargetKey"/>), so it must not
    /// render as "(pool)": e.g. a freshly-assigned Master whose lone TargetKey was
    /// mirrored into TargetKeys is a single target, not a pool.
    /// </summary>
    public static bool UsesPool(ChannelSettings ch) =>
        ch.TargetKeys != null &&
        ch.TargetKeys.FindAll(k => !string.IsNullOrWhiteSpace(k)).Count >= 2;
}
