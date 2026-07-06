using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>Unit tests for the pure inbound serial-protocol parser in Core.</summary>
public sealed class SerialProtocolTests
{
    [Fact]
    public void Parse_Hello_ExtractsAllFields()
    {
        DeviceMessage m = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.25,6,0x0000103A");

        m.Kind.Should().Be(DeviceMessageKind.Hello);
        m.Identity.Should().Be("PC_VOLUME_CONTROLLER");
        m.Protocol.Should().Be("2.25");
        m.ChannelCount.Should().Be(6);
        m.ChipId.Should().Be("0x0000103A");
    }

    [Fact]
    public void Parse_Hello_WithoutChipId_IsBackwardCompatible()
    {
        DeviceMessage m = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.24,6");

        m.Kind.Should().Be(DeviceMessageKind.Hello);
        m.Protocol.Should().Be("2.24");
        m.ChannelCount.Should().Be(6);
        m.ChipId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ENC,0,1", 0, 1)]
    [InlineData("ENC,5,-3", 5, -3)]
    public void Parse_Encoder_ExtractsChannelAndDelta(string line, int channel, int delta)
    {
        DeviceMessage m = SerialProtocol.Parse(line);
        m.Kind.Should().Be(DeviceMessageKind.EncoderTurn);
        m.Channel.Should().Be(channel);
        m.Delta.Should().Be(delta);
    }

    [Theory]
    [InlineData("BTN_SHORT,1", DeviceMessageKind.ButtonShort, 1)]
    [InlineData("BTN_LONG,2", DeviceMessageKind.ButtonLong, 2)]
    [InlineData("BTN_DOUBLE,3", DeviceMessageKind.ButtonDouble, 3)]
    [InlineData("BTN,4", DeviceMessageKind.ButtonShort, 4)] // legacy
    public void Parse_Buttons_ExtractsKindAndChannel(string line, DeviceMessageKind kind, int channel)
    {
        DeviceMessage m = SerialProtocol.Parse(line);
        m.Kind.Should().Be(kind);
        m.Channel.Should().Be(channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GARBAGE,1,2")]
    [InlineData("DBG,I2C scan start")]
    public void Parse_UnknownOrDebug_DoesNotThrowAndCategorises(string line)
    {
        DeviceMessage m = SerialProtocol.Parse(line);
        m.Kind.Should().BeOneOf(DeviceMessageKind.Unknown, DeviceMessageKind.Debug);
    }

    [Fact]
    public void IsValidIdentity_AcceptsMatchingNameAndProtocolAtOrAboveFloor()
    {
        DeviceMessage v225 = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.25,6,0x1");
        DeviceMessage v224 = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.24,6");

        SerialProtocol.IsValidIdentity(v225, "PC_VOLUME_CONTROLLER", "2.24").Should().BeTrue();
        SerialProtocol.IsValidIdentity(v224, "PC_VOLUME_CONTROLLER", "2.24").Should().BeTrue();
    }

    [Fact]
    public void IsValidIdentity_RejectsWrongNameOrTooOldProtocol()
    {
        DeviceMessage wrongName = SerialProtocol.Parse("HELLO,SOME_OTHER_DEVICE,2.25,6");
        DeviceMessage tooOld    = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.20,6");

        SerialProtocol.IsValidIdentity(wrongName, "PC_VOLUME_CONTROLLER", "2.24").Should().BeFalse();
        SerialProtocol.IsValidIdentity(tooOld, "PC_VOLUME_CONTROLLER", "2.24").Should().BeFalse();
    }

    [Fact]
    public void IsExpectedDevice_MatchesOnNameRegardlessOfProtocol()
    {
        // Our controller by name but with old firmware: recognised (so the host can
        // surface an incompatibility) yet not a valid identity to connect on.
        DeviceMessage tooOld = SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.20,6");

        SerialProtocol.IsExpectedDevice(tooOld, "PC_VOLUME_CONTROLLER").Should().BeTrue();
        SerialProtocol.IsValidIdentity(tooOld, "PC_VOLUME_CONTROLLER", "2.24").Should().BeFalse();
    }

    [Fact]
    public void IsExpectedDevice_RejectsWrongNameOrNonHello()
    {
        DeviceMessage wrongName = SerialProtocol.Parse("HELLO,SOME_OTHER_DEVICE,2.25,6");
        DeviceMessage notHello  = SerialProtocol.Parse("ENC,0,1");

        SerialProtocol.IsExpectedDevice(wrongName, "PC_VOLUME_CONTROLLER").Should().BeFalse();
        SerialProtocol.IsExpectedDevice(notHello, "PC_VOLUME_CONTROLLER").Should().BeFalse();
    }
}
