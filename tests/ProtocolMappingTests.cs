using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>Unit tests for the pure outbound protocol-string translations in Core.</summary>
public sealed class ProtocolMappingTests
{
    [Theory]
    [InlineData(DisplayModes.AppNameAndVolume, ProtocolCommands.DisplayModeAppVolume)]
    [InlineData(DisplayModes.LargeVolume, ProtocolCommands.DisplayModeLargeVolume)]
    [InlineData(DisplayModes.MuteStatus, ProtocolCommands.DisplayModeMuteStatus)]
    [InlineData(DisplayModes.AppOrDeviceName, ProtocolCommands.DisplayModeAppName)]
    [InlineData(DisplayModes.BarPercent, ProtocolCommands.DisplayModeBarPercent)]
    [InlineData("anything-unrecognised", ProtocolCommands.DisplayModeAppVolume)]
    public void DisplayModeToProtocol_MapsEachMode(string mode, string expected)
    {
        ProtocolMapping.DisplayModeToProtocol(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(DisplayModes.LargeVolume, ProtocolCommands.DisplayModeLargeVolume)]
    public void ChannelDisplayModeToProtocol_EmptyMeansInheritGlobal(string? mode, string expected)
    {
        ProtocolMapping.ChannelDisplayModeToProtocol(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(OledIdleActions.Off, "OFF")]
    [InlineData(OledIdleActions.DimTo10, "DIM_10")]
    [InlineData(OledIdleActions.DimTo30, "DIM_30")]
    [InlineData(OledIdleActions.DimTo70, "DIM_70")]
    [InlineData("", "OFF")]
    [InlineData(null, "OFF")]
    [InlineData("Garbage", "OFF")]
    public void IdleActionToProtocol_MapsDimAndOff(string? action, string expected)
    {
        ProtocolMapping.IdleActionToProtocol(action).Should().Be(expected);
    }

    [Fact]
    public void IdleActionToProtocol_ClampsDimRange()
    {
        ProtocolMapping.IdleActionToProtocol("DimTo5").Should().Be("DIM_10");
        ProtocolMapping.IdleActionToProtocol("DimTo90").Should().Be("DIM_70");
    }

    [Theory]
    [InlineData("Chrome", "Chrome")]
    [InlineData("  Spotify  ", "Spotify")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    [InlineData("has,comma,here", "has comma here")]
    public void MakeProtocolSafeLabel_SanitisesLabels(string? input, string expected)
    {
        ProtocolMapping.MakeProtocolSafeLabel(input).Should().Be(expected);
    }

    [Fact]
    public void MakeProtocolSafeLabel_CapsAt18Chars()
    {
        ProtocolMapping.MakeProtocolSafeLabel("ThisLabelIsWayTooLongToFit")
            .Should().HaveLength(18).And.Be("ThisLabelIsWayTooL");
    }
}
