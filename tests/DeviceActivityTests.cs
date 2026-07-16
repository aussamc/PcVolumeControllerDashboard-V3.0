using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Tests for <see cref="DeviceActivity.IsControllerUserActivity"/> — the classifier
/// the host's sleep/wake logic uses to decide which inbound messages count as the
/// user physically operating the controller. The regression that matters most: a
/// keepalive PONG arrives ~once a second, so it must NOT be treated as activity (that
/// would defeat auto-sleep entirely), while knob/button input and the controller's own
/// AWAKE notification must wake a sleeping controller.
/// </summary>
public sealed class DeviceActivityTests
{
    [Theory]
    [InlineData(DeviceMessageKind.EncoderTurn)]
    [InlineData(DeviceMessageKind.ButtonShort)]
    [InlineData(DeviceMessageKind.ButtonLong)]
    [InlineData(DeviceMessageKind.ButtonDouble)]
    [InlineData(DeviceMessageKind.Awake)]
    public void UserInputAndLocalWake_CountAsActivity(DeviceMessageKind kind)
    {
        DeviceActivity.IsControllerUserActivity(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData(DeviceMessageKind.Pong)]      // keepalive — must never wake, or auto-sleep is defeated
    [InlineData(DeviceMessageKind.Sleeping)]  // controller's ack of a SLEEP command
    [InlineData(DeviceMessageKind.Hello)]
    [InlineData(DeviceMessageKind.Debug)]
    [InlineData(DeviceMessageKind.Error)]
    [InlineData(DeviceMessageKind.Unknown)]
    public void KeepaliveAndHousekeeping_AreNotActivity(DeviceMessageKind kind)
    {
        DeviceActivity.IsControllerUserActivity(kind).Should().BeFalse();
    }
}
