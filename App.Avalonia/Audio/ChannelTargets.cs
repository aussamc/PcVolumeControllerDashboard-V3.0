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
        foreach (string k in pool)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            firstNonEmpty ??= k;
            if (audio.GetVolumeByKey(k) >= 0f) return k; // first live session wins
        }
        return firstNonEmpty ?? ch.TargetKey;
    }

    /// <summary>True if the channel has any assigned target (single or a non-empty pool).</summary>
    public static bool HasTarget(ChannelSettings ch) =>
        !string.IsNullOrWhiteSpace(ch.TargetKey) ||
        (ch.TargetKeys != null && ch.TargetKeys.Exists(k => !string.IsNullOrWhiteSpace(k)));

    /// <summary>True when the channel is in pool mode (has at least one pool entry).</summary>
    public static bool UsesPool(ChannelSettings ch) =>
        ch.TargetKeys != null && ch.TargetKeys.Exists(k => !string.IsNullOrWhiteSpace(k));
}
