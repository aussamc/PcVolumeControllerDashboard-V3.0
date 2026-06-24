using System.Globalization;

namespace PcVolumeControllerDashboard.Core;

/// <summary>Category of a parsed inbound (ESP32 → dashboard) serial message.</summary>
public enum DeviceMessageKind
{
    Unknown,
    Hello,
    EncoderTurn,
    ButtonShort,
    ButtonLong,
    ButtonDouble,
    Pong,
    Sleeping,
    Awake,
    Debug,
    Error,
}

/// <summary>
/// One parsed inbound serial message. Channel indices are 0-based as on the wire.
/// HELLO fields (<see cref="Identity"/>, <see cref="Protocol"/>,
/// <see cref="ChannelCount"/>, <see cref="ChipId"/>) are populated only for
/// <see cref="DeviceMessageKind.Hello"/>.
/// </summary>
public sealed record DeviceMessage
{
    public DeviceMessageKind Kind { get; init; }
    public string Raw { get; init; } = string.Empty;

    public int Channel { get; init; } = -1;
    public int Delta { get; init; }

    public string Identity { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public int ChannelCount { get; init; }
    public string ChipId { get; init; } = string.Empty;
}

/// <summary>
/// Pure parser for the inbound serial line protocol. Platform-neutral and free of
/// any connection/IO concerns so it can be unit-tested and shared across hosts.
/// </summary>
public static class SerialProtocol
{
    /// <summary>
    /// Parses one trimmed line into a <see cref="DeviceMessage"/>. Never throws;
    /// malformed or unrecognised lines yield <see cref="DeviceMessageKind.Unknown"/>.
    /// </summary>
    public static DeviceMessage Parse(string? line)
    {
        string raw = line?.Trim() ?? string.Empty;
        if (raw.Length == 0)
            return new DeviceMessage { Kind = DeviceMessageKind.Unknown, Raw = raw };

        string[] f = raw.Split(',');
        string cmd = f[0].Trim();

        switch (cmd)
        {
            case ProtocolCommands.Hello:
                return new DeviceMessage
                {
                    Kind         = DeviceMessageKind.Hello,
                    Raw          = raw,
                    Identity     = f.Length > 1 ? f[1].Trim() : string.Empty,
                    Protocol     = f.Length > 2 ? f[2].Trim() : string.Empty,
                    ChannelCount = f.Length > 3 && int.TryParse(f[3].Trim(), out int ch) ? ch : 0,
                    ChipId       = f.Length > 4 ? f[4].Trim() : string.Empty,
                };

            case ProtocolCommands.EncoderTurn:
                return new DeviceMessage
                {
                    Kind    = DeviceMessageKind.EncoderTurn,
                    Raw     = raw,
                    Channel = ParseInt(f, 1),
                    Delta   = ParseInt(f, 2),
                };

            case ProtocolCommands.ButtonShort:
            case ProtocolCommands.ButtonLegacy: // legacy "BTN" maps to a short press
                return new DeviceMessage { Kind = DeviceMessageKind.ButtonShort, Raw = raw, Channel = ParseInt(f, 1) };

            case ProtocolCommands.ButtonLong:
                return new DeviceMessage { Kind = DeviceMessageKind.ButtonLong, Raw = raw, Channel = ParseInt(f, 1) };

            case ProtocolCommands.ButtonDouble:
                return new DeviceMessage { Kind = DeviceMessageKind.ButtonDouble, Raw = raw, Channel = ParseInt(f, 1) };

            case ProtocolCommands.Pong:     return new DeviceMessage { Kind = DeviceMessageKind.Pong, Raw = raw };
            case ProtocolCommands.Sleeping: return new DeviceMessage { Kind = DeviceMessageKind.Sleeping, Raw = raw };
            case ProtocolCommands.Awake:    return new DeviceMessage { Kind = DeviceMessageKind.Awake, Raw = raw };
            case ProtocolCommands.Debug:    return new DeviceMessage { Kind = DeviceMessageKind.Debug, Raw = raw };
            case ProtocolCommands.Error:    return new DeviceMessage { Kind = DeviceMessageKind.Error, Raw = raw };

            default:
                return new DeviceMessage { Kind = DeviceMessageKind.Unknown, Raw = raw };
        }
    }

    /// <summary>
    /// True if a HELLO message satisfies the identity handshake: exact device name
    /// and a protocol version at or above the required floor.
    /// </summary>
    public static bool IsValidIdentity(DeviceMessage hello, string expectedName, string minProtocol)
    {
        if (hello.Kind != DeviceMessageKind.Hello) return false;
        if (!string.Equals(hello.Identity, expectedName, StringComparison.Ordinal)) return false;
        return CompareProtocol(hello.Protocol, minProtocol) >= 0;
    }

    private static int ParseInt(string[] fields, int index) =>
        index < fields.Length && int.TryParse(fields[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : -1;

    /// <summary>Compares dotted version strings numerically (e.g. "2.25" vs "2.24").</summary>
    private static int CompareProtocol(string a, string b)
    {
        if (Version.TryParse(Normalize(a), out Version? va) && Version.TryParse(Normalize(b), out Version? vb))
            return va.CompareTo(vb);
        return string.CompareOrdinal(a, b);
    }

    // Version.TryParse needs at least major.minor; pad a bare integer.
    private static string Normalize(string v) => v.Contains('.') ? v : v + ".0";
}
