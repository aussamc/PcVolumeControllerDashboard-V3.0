using FluentAssertions;
using PcVolumeControllerDashboard.Core;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Covers the run-at-login path guard. Both "transient" cases below were observed in
/// the wild: the app wrote its own updater-staging path and its own bin\Debug path
/// into the HKCU Run entry, which then silently launched nothing at the next logon.
/// </summary>
public class StartupPathPolicyTests
{
    private const string Temp = @"C:\Users\sam\AppData\Local\Temp\";

    [Fact]
    public void InstalledLocation_IsNotTransient()
    {
        StartupPathPolicy.IsTransientLocation(
            @"C:\Program Files\PC Volume Controller Dashboard\PcVolumeControllerDashboard.Avalonia.exe", Temp)
            .Should().BeFalse();
    }

    [Fact]
    public void UpdaterStagingDirectory_IsTransient()
    {
        StartupPathPolicy.IsTransientLocation(
            Temp + @"PcVolumeController-update\PcVolumeControllerDashboard.Avalonia.exe", Temp)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\src\App.Avalonia\bin\Debug\net10.0-windows10.0.17763.0\app.exe")]
    [InlineData(@"C:\src\App.Avalonia\bin\Release\net10.0\app.exe")]
    [InlineData(@"C:\src\App.Avalonia\BIN\debug\net10.0\app.exe")]
    public void BuildOutputDirectory_IsTransient(string path)
    {
        StartupPathPolicy.IsTransientLocation(path, Temp).Should().BeTrue();
    }

    [Fact]
    public void PortableBuildInAnOrdinaryFolder_IsNotTransient()
    {
        // A portable exe run from Downloads or a USB stick is a legitimate, stable
        // choice — the guard must not block it.
        StartupPathPolicy.IsTransientLocation(@"C:\Users\sam\Downloads\app.exe", Temp)
            .Should().BeFalse();
    }

    [Fact]
    public void SiblingDirectorySharingTheTempPrefix_IsNotTransient()
    {
        // "C:\...\Temp2" must not be read as living under "C:\...\Temp".
        StartupPathPolicy.IsTransientLocation(@"C:\Users\sam\AppData\Local\Temp2\app.exe", Temp)
            .Should().BeFalse();
    }

    [Fact]
    public void FolderMerelyContainingTheWordRelease_IsNotTransient()
    {
        StartupPathPolicy.IsTransientLocation(@"C:\Tools\Release Notes\app.exe", Temp)
            .Should().BeFalse();
    }

    [Fact]
    public void BinWithoutADebugOrReleaseChild_IsNotTransient()
    {
        StartupPathPolicy.IsTransientLocation(@"C:\Tools\bin\app.exe", Temp).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPath_IsTransient(string? path)
    {
        // Nothing safe to record either way.
        StartupPathPolicy.IsTransientLocation(path, Temp).Should().BeTrue();
    }

    [Fact]
    public void BlankTempDirectory_StillEvaluatesBuildOutput()
    {
        StartupPathPolicy.IsTransientLocation(@"C:\src\bin\Debug\net10.0\app.exe", null)
            .Should().BeTrue();
        StartupPathPolicy.IsTransientLocation(@"C:\Program Files\App\app.exe", null)
            .Should().BeFalse();
    }
}
