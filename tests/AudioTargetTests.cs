using FluentAssertions;
using PcVolumeControllerDashboard.Core.Audio;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the platform-neutral <see cref="AudioTarget"/> DTO that
/// replaces the WPF host's NAudio-coupled AudioTargetItem.
/// </summary>
public sealed class AudioTargetTests
{
    [Fact]
    public void CreateMaster_IsMasterEndpoint()
    {
        AudioTarget master = AudioTarget.CreateMaster();

        master.Key.Should().Be("MASTER");
        master.IsMaster.Should().BeTrue();
        master.IsMicInput.Should().BeFalse();
        master.IsActiveOrMaster.Should().BeTrue();
    }

    [Fact]
    public void CreateMic_IsCaptureEndpoint()
    {
        AudioTarget mic = AudioTarget.CreateMic();

        mic.Key.Should().Be("MIC_INPUT");
        mic.IsMicInput.Should().BeTrue();
        mic.IsMaster.Should().BeFalse();
        mic.IsActiveOrMaster.Should().BeTrue();
    }

    [Fact]
    public void IsActiveOrMaster_TrueForLiveStream()
    {
        var target = new AudioTarget { Key = "PROC:chrome", IsLive = true };
        target.IsActiveOrMaster.Should().BeTrue();
    }

    [Fact]
    public void IsActiveOrMaster_FalseForOfflinePlaceholder()
    {
        // A saved target with no running stream (the "Waiting for app" row).
        var target = new AudioTarget { Key = "PROC:chrome", IsLive = false, State = "Waiting for app" };
        target.IsActiveOrMaster.Should().BeFalse();
    }

    [Fact]
    public void IsActiveOrMaster_TrueForVoiceMeeter()
    {
        var target = new AudioTarget { Key = "VM_STRIP:0", IsVoiceMeeter = true };
        target.IsActiveOrMaster.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(42, "42%")]
    [InlineData(100, "100%")]
    public void VolumeDisplay_FormatsAsPercent(int volume, string expected)
    {
        new AudioTarget { Volume = volume }.VolumeDisplay.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void MuteDisplay_FormatsYesNo(bool muted, string expected)
    {
        new AudioTarget { Muted = muted }.MuteDisplay.Should().Be(expected);
    }

    [Fact]
    public void ToString_ReturnsLabel()
    {
        new AudioTarget { Label = "Spotify" }.ToString().Should().Be("Spotify");
    }
}
