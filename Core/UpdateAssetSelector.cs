using System;
using System.Collections.Generic;
using System.Linq;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// The install medium the running dashboard should update itself with. Resolved by the
/// host from the compile TFM + runtime (Windows installer, or — on Linux — whichever of
/// the AppImage / .deb matches how the app is running); macOS has no signed artifact yet.
/// </summary>
public enum UpdatePlatform
{
    /// <summary>No self-update artifact for this platform (macOS today) — open the release page.</summary>
    Unsupported = 0,
    Windows,
    LinuxAppImage,
    LinuxDeb,
}

/// <summary>
/// Pure release-asset picker: given the files attached to a GitHub release and the
/// resolved <see cref="UpdatePlatform"/>, chooses the one to download. Host-free so the
/// matching rules (which are tied to the release workflow's asset names) are unit-tested
/// without a network call. The naming contract, per the release workflows:
/// <list type="bullet">
/// <item>Windows installer — <c>PcVolumeControllerDashboard-Setup-&lt;ver&gt;.exe</c></item>
/// <item>Windows portable — <c>PcVolumeControllerDashboard.Avalonia.exe</c></item>
/// <item>Linux AppImage — <c>PcVolumeControllerDashboard-&lt;ver&gt;-x86_64.AppImage</c></item>
/// <item>Linux Debian — <c>pcvolumecontroller_&lt;ver&gt;_amd64.deb</c></item>
/// </list>
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>
    /// Returns the asset to download for <paramref name="platform"/>, or <c>null</c> when
    /// none matches (unsupported platform, or the expected asset is missing from the
    /// release). Windows prefers the installer and falls back to the portable exe.
    /// Matching is case-insensitive on the file extension / marker.
    /// </summary>
    public static ReleaseAsset? Select(IReadOnlyList<ReleaseAsset>? assets, UpdatePlatform platform)
    {
        if (assets is null || assets.Count == 0)
            return null;

        switch (platform)
        {
            case UpdatePlatform.Windows:
                // Prefer the Inno installer ("…-Setup-….exe"); fall back to any other .exe
                // (the portable single-file build) so a release missing the installer still
                // offers something to download.
                return assets.FirstOrDefault(a => EndsWith(a.Name, ".exe") && Contains(a.Name, "Setup"))
                    ?? assets.FirstOrDefault(a => EndsWith(a.Name, ".exe"));

            case UpdatePlatform.LinuxAppImage:
                return assets.FirstOrDefault(a => EndsWith(a.Name, ".AppImage"));

            case UpdatePlatform.LinuxDeb:
                return assets.FirstOrDefault(a => EndsWith(a.Name, ".deb"));

            default:
                return null;
        }
    }

    private static bool EndsWith(string? name, string suffix) =>
        name != null && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? name, string token) =>
        name != null && name.Contains(token, StringComparison.OrdinalIgnoreCase);
}
