namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// All serial protocol command strings exchanged between the dashboard and firmware.
/// Centralising them here means a protocol change only requires editing this one class.
/// </summary>
public static class ProtocolCommands
{
    // ── Inbound (ESP32 → dashboard) ──────────────────────────────────────────
    public const string Hello         = "HELLO";
    public const string Pong          = "PONG";
    public const string Debug         = "DBG";
    public const string EncoderTurn   = "ENC";
    public const string ButtonLegacy  = "BTN";
    public const string ButtonShort   = "BTN_SHORT";
    public const string ButtonLong    = "BTN_LONG";
    public const string ButtonDouble  = "BTN_DOUBLE";
    public const string Sleeping      = "SLEEPING";
    public const string Awake         = "AWAKE";
    public const string OledCfgAck    = "OLEDCFG_ACK";
    public const string OledIdleStart = "OLED_IDLE_START";
    public const string OledIdleEnd   = "OLED_IDLE_END";
    public const string Error         = "ERR";

    // ── Outbound (dashboard → ESP32) ─────────────────────────────────────────
    public const string Sleep         = "SLEEP";
    public const string Wake          = "WAKE";
    public const string Ping          = "PING";
    public const string HelloQuery    = "HELLO?";
    public const string Disconnect    = "DISCONNECT";
    public const string ChannelState  = "CHSTATE";
    public const string DisplayMode   = "DISPMODE";
    public const string OledConfig    = "OLEDCFG";
    public const string TestDisplay   = "TEST_DISPLAY";
    public const string ShowIdent     = "SHOW_IDENT";

    // ── Display mode protocol values ─────────────────────────────────────────
    public const string DisplayModeAppVolume   = "APP_VOLUME";
    public const string DisplayModeLargeVolume = "LARGE_VOLUME";
    public const string DisplayModeMuteStatus  = "MUTE_STATUS";
    public const string DisplayModeAppName     = "APP_OR_DEVICE_NAME";
    public const string DisplayModeBarPercent  = "BAR_PERCENT";
}
