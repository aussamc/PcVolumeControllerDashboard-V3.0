namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Accumulates per-channel evidence that each physical encoder and button on the
/// controller registers, for the Debug tab's hardware self-test (parity item Q4).
/// Pure and platform-neutral so it's unit-testable: the host feeds it parsed
/// inbound <see cref="DeviceMessage"/>s (from the connection's MessageReceived
/// stream) and renders <see cref="FormatChannel"/> into a per-channel checklist.
///
/// Channel indices are 0-based on the wire (matching <see cref="DeviceMessage.Channel"/>);
/// <see cref="FormatChannel"/> presents them 1-based for the UI (standing rule #4).
/// Not thread-safe — callers marshal to a single thread before recording/reading.
/// </summary>
public sealed class HardwareSelfTest
{
    private readonly int[] _encoderCounts;
    private readonly bool[] _buttonSeen;

    /// <summary>Number of channels tracked (1-based labels run 1..ChannelCount).</summary>
    public int ChannelCount { get; }

    public HardwareSelfTest(int channelCount)
    {
        ChannelCount = channelCount < 0 ? 0 : channelCount;
        _encoderCounts = new int[ChannelCount];
        _buttonSeen = new bool[ChannelCount];
    }

    /// <summary>Encoder events seen on a 0-based channel (0 if out of range).</summary>
    public int EncoderCount(int channel) =>
        channel >= 0 && channel < ChannelCount ? _encoderCounts[channel] : 0;

    /// <summary>True if any button press has been seen on a 0-based channel.</summary>
    public bool ButtonSeen(int channel) =>
        channel >= 0 && channel < ChannelCount && _buttonSeen[channel];

    /// <summary>
    /// Records an inbound device message. Counts an encoder turn (any delta) and
    /// marks a button (short/long/double) as seen; ignores everything else and any
    /// out-of-range channel. Returns true if the tally changed, so the UI can
    /// re-render only on a real update.
    /// </summary>
    public bool Record(DeviceMessage message)
    {
        int ch = message.Channel;
        if (ch < 0 || ch >= ChannelCount) return false;

        switch (message.Kind)
        {
            case DeviceMessageKind.EncoderTurn:
                _encoderCounts[ch]++;
                return true;
            case DeviceMessageKind.ButtonShort:
            case DeviceMessageKind.ButtonLong:
            case DeviceMessageKind.ButtonDouble:
                if (_buttonSeen[ch]) return false;
                _buttonSeen[ch] = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Clears all recorded encoder counts and button-seen flags.</summary>
    public void Reset()
    {
        Array.Clear(_encoderCounts);
        Array.Clear(_buttonSeen);
    }

    /// <summary>
    /// Formats one channel's checklist line (channel shown 1-based), e.g.
    /// <c>"Channel 3: encoder count 12, button seen yes"</c>.
    /// </summary>
    public string FormatChannel(int channel) =>
        $"Channel {channel + 1}: encoder count {EncoderCount(channel)}, " +
        $"button seen {(ButtonSeen(channel) ? "yes" : "no")}";

    /// <summary>Formats every channel's line, newline-joined, for a summary block.</summary>
    public string FormatAll()
    {
        var lines = new string[ChannelCount];
        for (int i = 0; i < ChannelCount; i++)
            lines[i] = FormatChannel(i);
        return string.Join("\n", lines);
    }
}
