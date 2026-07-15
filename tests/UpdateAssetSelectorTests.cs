using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure <see cref="UpdateAssetSelector"/> that picks the right release
/// download for the running platform (v3.19 download-and-apply engine). Uses the real
/// asset names the release workflows produce.
/// </summary>
public sealed class UpdateAssetSelectorTests
{
    // The four assets attached to a real release (see the v3.18 release).
    private static IReadOnlyList<ReleaseAsset> FullRelease() => new[]
    {
        new ReleaseAsset { Name = "PcVolumeControllerDashboard-3.19-x86_64.AppImage", DownloadUrl = "u1", Size = 1 },
        new ReleaseAsset { Name = "PcVolumeControllerDashboard-Setup-3.19.exe", DownloadUrl = "u2", Size = 2 },
        new ReleaseAsset { Name = "PcVolumeControllerDashboard.Avalonia.exe", DownloadUrl = "u3", Size = 3 },
        new ReleaseAsset { Name = "pcvolumecontroller_3.19_amd64.deb", DownloadUrl = "u4", Size = 4 },
    };

    [Fact]
    public void Windows_PrefersTheInstaller()
    {
        UpdateAssetSelector.Select(FullRelease(), UpdatePlatform.Windows)!
            .Name.Should().Be("PcVolumeControllerDashboard-Setup-3.19.exe");
    }

    [Fact]
    public void Windows_FallsBackToPortableExe_WhenNoInstaller()
    {
        var assets = new[]
        {
            new ReleaseAsset { Name = "PcVolumeControllerDashboard.Avalonia.exe", DownloadUrl = "u", Size = 1 },
            new ReleaseAsset { Name = "pcvolumecontroller_3.19_amd64.deb", DownloadUrl = "u", Size = 1 },
        };
        UpdateAssetSelector.Select(assets, UpdatePlatform.Windows)!
            .Name.Should().Be("PcVolumeControllerDashboard.Avalonia.exe");
    }

    [Fact]
    public void LinuxAppImage_PicksTheAppImage()
    {
        UpdateAssetSelector.Select(FullRelease(), UpdatePlatform.LinuxAppImage)!
            .Name.Should().EndWith(".AppImage");
    }

    [Fact]
    public void LinuxDeb_PicksTheDeb()
    {
        UpdateAssetSelector.Select(FullRelease(), UpdatePlatform.LinuxDeb)!
            .Name.Should().EndWith(".deb");
    }

    [Fact]
    public void Unsupported_ReturnsNull()
    {
        UpdateAssetSelector.Select(FullRelease(), UpdatePlatform.Unsupported).Should().BeNull();
    }

    [Fact]
    public void MissingAsset_ReturnsNull()
    {
        // A Linux-only release offers nothing to a Windows client.
        var linuxOnly = new[] { new ReleaseAsset { Name = "app_1.0_amd64.deb", DownloadUrl = "u", Size = 1 } };
        UpdateAssetSelector.Select(linuxOnly, UpdatePlatform.Windows).Should().BeNull();
    }

    [Fact]
    public void EmptyOrNull_ReturnsNull()
    {
        UpdateAssetSelector.Select(Array.Empty<ReleaseAsset>(), UpdatePlatform.Windows).Should().BeNull();
        UpdateAssetSelector.Select(null, UpdatePlatform.LinuxDeb).Should().BeNull();
    }
}
