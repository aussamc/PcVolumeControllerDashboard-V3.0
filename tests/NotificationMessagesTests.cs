using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure desktop-notification body formatters in Core (parity item F6).
/// </summary>
public sealed class NotificationMessagesTests
{
    [Fact]
    public void Connected_IncludesProtocolAndChip()
    {
        NotificationMessages.Connected("2.25", "0x0000103A")
            .Should().Be("Connected — protocol 2.25, chip 0x0000103A.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Connected_MissingProtocol_SaysUnknown(string? protocol)
    {
        NotificationMessages.Connected(protocol, "0xABC")
            .Should().Be("Connected — protocol unknown, chip 0xABC.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Connected_MissingChip_SaysNone(string? chip)
    {
        NotificationMessages.Connected("2.25", chip)
            .Should().Be("Connected — protocol 2.25, chip (none).");
    }

    [Fact]
    public void Connected_TrimsValues()
    {
        NotificationMessages.Connected("  2.25  ", "  0xABC  ")
            .Should().Be("Connected — protocol 2.25, chip 0xABC.");
    }

    [Fact]
    public void Disconnected_IsStable()
    {
        NotificationMessages.Disconnected().Should().Be("The controller was disconnected.");
    }

    [Fact]
    public void NullNotificationService_ShowDoesNotThrow()
    {
        var svc = new NullNotificationService();
        var act = () => svc.Show("t", "m");
        act.Should().NotThrow();
    }
}
