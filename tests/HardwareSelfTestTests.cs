using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="HardwareSelfTest"/> — the Debug tab's per-channel
/// encoder/button tally (parity item Q4).
/// </summary>
public sealed class HardwareSelfTestTests
{
    private static DeviceMessage Enc(int channel, int delta = 1) =>
        SerialProtocol.Parse($"ENC,{channel},{delta}");

    private static DeviceMessage BtnShort(int channel) =>
        SerialProtocol.Parse($"BTN_SHORT,{channel}");

    [Fact]
    public void NewTally_IsAllZeroAndUnseen()
    {
        var t = new HardwareSelfTest(6);

        for (int i = 0; i < 6; i++)
        {
            t.EncoderCount(i).Should().Be(0);
            t.ButtonSeen(i).Should().BeFalse();
        }
    }

    [Fact]
    public void Record_EncoderTurns_AccumulatesPerChannel()
    {
        var t = new HardwareSelfTest(6);

        t.Record(Enc(2)).Should().BeTrue();
        t.Record(Enc(2, -1)).Should().BeTrue(); // direction doesn't matter — it's a count

        t.EncoderCount(2).Should().Be(2);
        t.EncoderCount(0).Should().Be(0);
    }

    [Fact]
    public void Record_Button_MarksSeen_AndDeDupes()
    {
        var t = new HardwareSelfTest(6);

        t.Record(BtnShort(4)).Should().BeTrue();      // first press changes state
        t.Record(BtnShort(4)).Should().BeFalse();     // already seen — no change
        t.Record(SerialProtocol.Parse("BTN_LONG,4")).Should().BeFalse();

        t.ButtonSeen(4).Should().BeTrue();
        t.ButtonSeen(3).Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public void Record_OutOfRangeChannel_IsIgnored(int channel)
    {
        var t = new HardwareSelfTest(6);

        t.Record(Enc(channel)).Should().BeFalse();
        t.Record(BtnShort(channel)).Should().BeFalse();
    }

    [Fact]
    public void Record_NonEncoderNonButton_IsIgnored()
    {
        var t = new HardwareSelfTest(6);

        t.Record(SerialProtocol.Parse("PONG")).Should().BeFalse();
        t.Record(SerialProtocol.Parse("HELLO,PC_VOLUME_CONTROLLER,2.25,6")).Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = new HardwareSelfTest(6);
        t.Record(Enc(0));
        t.Record(BtnShort(1));

        t.Reset();

        t.EncoderCount(0).Should().Be(0);
        t.ButtonSeen(1).Should().BeFalse();
    }

    [Fact]
    public void FormatChannel_IsOneBased_AndReportsCountAndButton()
    {
        var t = new HardwareSelfTest(6);
        t.Record(Enc(0));
        t.Record(Enc(0));
        t.Record(BtnShort(0));

        // Channel 0 on the wire is presented as "Channel 1" in the UI (standing rule #4).
        t.FormatChannel(0).Should().Be("Channel 1: encoder count 2, button seen yes");
        t.FormatChannel(5).Should().Be("Channel 6: encoder count 0, button seen no");
    }

    [Fact]
    public void FormatAll_HasOneLinePerChannel()
    {
        var t = new HardwareSelfTest(6);

        t.FormatAll().Split('\n').Should().HaveCount(6);
    }
}
