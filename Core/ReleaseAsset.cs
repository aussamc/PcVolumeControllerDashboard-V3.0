namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// A single downloadable file attached to a GitHub release (the Windows installer /
/// portable exe, the Linux .deb / AppImage). Populated by the host's update check from
/// the Releases API; consumed by the pure <see cref="UpdateAssetSelector"/> to pick the
/// right file for the running platform and by the host downloader to fetch + verify it.
/// </summary>
public sealed record ReleaseAsset
{
    /// <summary>The asset file name (e.g. <c>PcVolumeControllerDashboard-Setup-3.19.exe</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The direct browser download URL for the asset.</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>Asset size in bytes (used as a cheap integrity check after download).</summary>
    public long Size { get; init; }

    /// <summary>
    /// The API-reported content digest, e.g. <c>sha256:abcd…</c>, or null when the API
    /// doesn't provide one. Parsed by <see cref="AssetDigest"/> for post-download SHA-256
    /// verification.
    /// </summary>
    public string? Digest { get; init; }
}
