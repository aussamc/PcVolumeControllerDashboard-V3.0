using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateDownloadCleanup"/>, the pure "which downloaded update
/// files are stale" decision used to keep the update temp folder from accumulating one
/// installer per release (v3.19.3). The host does the actual deletes.
/// </summary>
public sealed class UpdateDownloadCleanupTests
{
    private const string Dir = @"C:\tmp\PcVolumeController-update";

    private static string P(string name) => Path.Combine(Dir, name);

    [Fact]
    public void SelectStale_ReturnsEveryFileExceptTheKeptOne()
    {
        string[] files =
        {
            P("PcVolumeControllerDashboard-Setup-3.19.1.exe"),
            P("PcVolumeControllerDashboard-Setup-3.19.2.exe"),
            P("PcVolumeControllerDashboard-Setup-3.19.3.exe"),
        };

        UpdateDownloadCleanup.SelectStale(files, "PcVolumeControllerDashboard-Setup-3.19.3.exe")
            .Should().BeEquivalentTo(new[]
            {
                P("PcVolumeControllerDashboard-Setup-3.19.1.exe"),
                P("PcVolumeControllerDashboard-Setup-3.19.2.exe"),
            });
    }

    [Fact]
    public void SelectStale_OnlyKeptFilePresent_ReturnsNothing()
    {
        string[] files = { P("keep.exe") };
        UpdateDownloadCleanup.SelectStale(files, "keep.exe").Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_MatchesKeptNameCaseInsensitively()
    {
        string[] files = { P("Setup.EXE"), P("old.exe") };
        UpdateDownloadCleanup.SelectStale(files, "setup.exe")
            .Should().ContainSingle().Which.Should().Be(P("old.exe"));
    }

    [Fact]
    public void SelectStale_ComparesOnFileNameNotFullPath()
    {
        // A same-named file in a different folder is still "the kept file" by name.
        string[] files = { Path.Combine(@"C:\other", "keep.exe"), P("stale.exe") };
        UpdateDownloadCleanup.SelectStale(files, "keep.exe")
            .Should().ContainSingle().Which.Should().Be(P("stale.exe"));
    }

    [Fact]
    public void SelectStale_BlankKeepName_TreatsAllAsStale()
    {
        string[] files = { P("a.exe"), P("b.exe") };
        UpdateDownloadCleanup.SelectStale(files, "").Should().HaveCount(2);
    }

    [Fact]
    public void SelectStale_SkipsNullOrEmptyEntries()
    {
        string[] files = { P("a.exe"), "", null! };
        UpdateDownloadCleanup.SelectStale(files, "keep.exe")
            .Should().ContainSingle().Which.Should().Be(P("a.exe"));
    }

    [Fact]
    public void SelectStale_EmptyInput_ReturnsEmpty()
    {
        UpdateDownloadCleanup.SelectStale(Enumerable.Empty<string>(), "keep.exe").Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_NullInput_Throws()
    {
        Action act = () => UpdateDownloadCleanup.SelectStale(null!, "keep.exe");
        act.Should().Throw<ArgumentNullException>();
    }
}
