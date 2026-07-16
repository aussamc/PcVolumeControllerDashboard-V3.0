// =============================================================================
// PC Volume Controller — firmware v2.28
// Target: ESP32-S3-DevKitC-1-N16R8 (custom carrier + display PCB, v1.4)
//
// Changes from v2.27:
//   - LARGE_VOLUME anti-burn-in fix: the size-2 channel-name header sat flush at
//     row 0 with no top margin, so the SETDISPLAYOFFSET anti-burn shift could wrap
//     its top row by ~1px. The header is now drawn at y3 and the rule at y20, so
//     all LARGE_VOLUME content lives within rows 3..60 — a full 0..3px shift can no
//     longer wrap a lit pixel off either edge. The dashboard's Core OLED preview
//     (OledRenderer.RenderLargeVolume) mirrors this exactly.
//   - Version bumped to 2.28 (display-only change; reflash required to see it).
//     The wire protocol is unchanged from 2.27 — the dashboard's required
//     protocol floor (2.24) still accepts this firmware.
//
// Changes from v2.26:
//   - Startup firmware-version splash: on boot every OLED shows the firmware
//     version ("Firmware / v2.27") for 2 seconds so the flashed version is easy
//     to confirm at a glance, then falls through to the usual "Waiting for PC"
//     screen (or live channel state if the PC has already pushed it).  The hold
//     is non-blocking (a millis() deadline checked in loop(), not a delay()), so
//     serial keeps being serviced during it — HELLO is still sent immediately at
//     the end of setup(), well inside the dashboard's identify window, and any
//     config the PC pushes is buffered and applied the moment the splash ends.
//   - Version bumped to 2.27 (display-only change; reflash required to see it).
//     The wire protocol is unchanged from 2.26 — the dashboard's required
//     protocol floor (2.24) still accepts this firmware.
//
// Changes from v2.25:
//   - LARGE_VOLUME display mode redesigned so it reads as genuinely "large":
//     the channel name is now size 2, the volume number is size 4, and the
//     separate "Muted/Unmuted" bottom strip is removed.  When the channel is
//     muted the big number is replaced by the word "MUTE" (same size 4);
//     when unmuted the number returns.  The dashboard's Core OLED preview
//     (OledRenderer.RenderLargeVolume) mirrors this pixel layout exactly.
//   - Anti-burn-in no longer clips the bottom text: every display mode now keeps
//     its content within rows 0..60 (the two modes with a bottom line at y56 —
//     APP_VOLUME and BAR_PERCENT — were lifted to y54), reserving the bottom 3 rows
//     so the 0..3px SETDISPLAYOFFSET shift only ever wraps empty rows.
//   - Version bumped to 2.26 (display-only change; reflash required to see it).
//     The wire protocol is unchanged from 2.25 — the dashboard's required
//     protocol floor (2.24) still accepts this firmware.
//
// Changes from v2.24:
//   - HELLO message now includes the ESP32 chip ID as a 5th comma-separated
//     field (format: "0xXXXXXXXX").  The dashboard uses this to pair and
//     verify the correct controller is connected.  Without this field the
//     "Paired controller" feature in the dashboard always shows "(none)".
//   - Protocol version bumped to 2.25 (reflash required for pairing to work)
// =============================================================================

#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>   // SSD1315 is command-compatible with SSD1306

// =============================================================================
// Firmware identity
// =============================================================================
#define FIRMWARE_NAME    "PC_VOLUME_CONTROLLER"
#define PROTOCOL_VERSION "2.28"
#define CHANNEL_COUNT    6

// =============================================================================
// Startup options
//   I2C_SCAN_ON_STARTUP — set true to scan the I2C bus on boot and print
//   all found devices to the dashboard debug log via DBG messages.
//   Also available at any time via the SCAN_I2C serial command.
// =============================================================================
#define I2C_SCAN_ON_STARTUP    true

// =============================================================================
// Hardware pins — ESP32-S3-DevKitC-1-N16R8 carrier PCB v1.4
//
// I2C:   SDA=9, SCL=10  (shared by all OLEDs through TCA9548A mux)
// USB:   GPIO 19/20 reserved for USB CDC D-/D+, NOT available for GPIO use
//
// Encoder pin assignments (A=CLK, B=DT, SW=button):
//   Ch0:  A=13  B=12  SW=14
//   Ch1:  A=3   B=21  SW=11
//   Ch2:  A=18  B=17  SW=8
//   Ch3:  A=40  B=41  SW=38
//   Ch4:  A=7   B=15  SW=16
//   Ch5:  A=6   B=5   SW=4
// =============================================================================
#define PIN_I2C_SDA 9
#define PIN_I2C_SCL 10

const uint8_t ENC_A_PIN[CHANNEL_COUNT]  = { 13,  3, 18, 40,  7,  6 };
const uint8_t ENC_B_PIN[CHANNEL_COUNT]  = { 12, 21, 17, 41, 15,  5 };
const uint8_t ENC_SW_PIN[CHANNEL_COUNT] = { 14, 11,  8, 38, 16,  4 };

// =============================================================================
// TCA9548A I2C mux
//   Address 0x70 (A0-A2 all low on PCB).
//   Write (1 << N) to select physical channel N; write 0 to deselect all.
//
//   MUX_CHANNEL_MAP — maps logical software channel (0-5) to the physical
//   mux output that drives that channel's OLED.  Edit this table if the
//   display PCB wiring doesn't match the encoder order.
//
//   Current mapping (after physical wiring corrections):
//     logical ch:   0  1  2  3  4  5
//     physical mux: 3  2  1  6  5  4
//   (swaps: OLED1<->4, OLED2<->3, OLED5<->6; OLED4->MUX6)
// =============================================================================
#define MUX_I2C_ADDR 0x70

const uint8_t MUX_CHANNEL_MAP[CHANNEL_COUNT] = { 3, 2, 1, 6, 5, 4 };

// =============================================================================
// OLED displays (SSD1315, one per channel, all at address 0x3C)
//   A single Adafruit_SSD1306 object is used. The shared framebuffer is
//   rebuilt per channel then pushed via display.display() after selecting
//   the correct mux channel.
// =============================================================================
#define SCREEN_WIDTH   128
#define SCREEN_HEIGHT   64
#define OLED_RESET      -1
#define OLED_I2C_ADDR 0x3C

Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET);
bool displayOk[CHANNEL_COUNT];     // true if that OLED initialised successfully

// =============================================================================
// Global sleep / display state
// =============================================================================
bool   displaySleeping       = false;
bool   sleepBlocksFirstInput = false;
String sleepReason           = "";
String serialInputBuffer     = "";

// Startup firmware-version splash (v2.27).
//   While active, every OLED shows the firmware version and updateDisplay() is
//   suppressed so incoming PC state doesn't overwrite it early — the state is
//   still stored, just drawn once the splash ends. Non-blocking: loop() clears
//   it once millis() passes the 2 s deadline.
bool          bootSplashActive  = false;
unsigned long bootSplashUntilMs = 0;
const unsigned long BOOT_SPLASH_MS = 2000;

// OLED configuration — global defaults applied by OLEDCFG command.
String displayMode                = "AppVolume";
int    oledBrightnessPercent      = 100;
int    activeOledBrightnessPercent = 100;
bool   connectedIdleActive        = false;
String connectedIdleAction        = "DIM_30";
unsigned long connectedIdleTimeoutMs = 600000UL;
unsigned long lastDisplayActivityMs  = 0;
bool    antiBurnInEnabled = true;
uint8_t lastBurnInOffset  = 255;

// Per-channel OLED display mode overrides.
//   Empty string = inherit global displayMode set by OLEDCFG.
//   Non-empty = use this mode for this channel regardless of global setting.
//   Set by DISPMODE,<ch>,<mode> from the dashboard.
String channelMode[CHANNEL_COUNT];   // default-initialised to "" (Arduino String default)

// =============================================================================
// Per-channel encoder state
// =============================================================================
struct EncoderState {
  uint8_t       lastState;
  int8_t        accumulator;
  unsigned long lastTransitionUs;
  unsigned long lastReportMs;
};
EncoderState encoders[CHANNEL_COUNT];

// DIAGNOSTIC: both debounce constants set to 0 to disable their respective guards.
//   ENC_DEBOUNCE_US=0   — the condition (elapsed < 0) is never true for unsigned long,
//                         so the 1 ms transition gate is fully bypassed.
//   ENC_REPORT_GUARD_MS=0 — the condition (elapsed >= 0) is always true for unsigned
//                         long, so the 3 ms report rate-limit is fully bypassed.
const unsigned long ENC_DEBOUNCE_US     = 0;
const unsigned long ENC_REPORT_GUARD_MS = 0;
const int8_t        ENC_COUNTS_PER_DETENT = 4;

// =============================================================================
// Per-channel button state
// =============================================================================
struct ButtonState {
  bool          lastState;
  bool          pressed;
  unsigned long downMs;
  unsigned long lastChangeMs;
  unsigned long lastShortPressMs;   // timestamp of last short press, used for double-press detection
};
ButtonState buttons[CHANNEL_COUNT];

const unsigned long BTN_DEBOUNCE_MS = 50;
const unsigned long BTN_LONG_MS     = 650;
const unsigned long BTN_DOUBLE_MS   = 350;  // max gap between two short presses to count as double

// =============================================================================
// PC connection / watchdog
// =============================================================================
bool          pcConnected       = false;
unsigned long lastPcMessageMs   = 0;
const unsigned long PC_TIMEOUT_MS = 3000;
unsigned long lastLocalActivityMs = 0;
unsigned long noDashboardSleepMs  = 120000;

// =============================================================================
// Per-channel display state
//   Populated by STATE and CHSTATE messages from the dashboard.
//   Each OLED renders its own channel entry.
// =============================================================================
struct ChannelState {
  String label;
  int    volume;
  bool   muted;
  String status;
};
ChannelState channels[CHANNEL_COUNT];

// =============================================================================
// Forward declarations
// =============================================================================
void updateDisplay(int ch);
void updateAllDisplays();
void applyAntiBurnInOffset();
void markLocalActivity();
void markDisplayActivity(const String &reason);
void showFirmwareVersionSplash();
void showWaitingSplash();
void endBootSplash();

// =============================================================================
// Mux helpers
// =============================================================================

// Raw physical channel select — use this only when you need to address a
// specific mux output directly (e.g. I2C bus scanning).
void selectMuxChannel(uint8_t physicalCh) {
  Wire.beginTransmission(MUX_I2C_ADDR);
  Wire.write(1 << physicalCh);
  Wire.endTransmission();
}

// Logical display channel select — applies MUX_CHANNEL_MAP so that
// software channel N drives the correct physical OLED regardless of
// how the display PCB happens to be wired.
void selectDisplayChannel(uint8_t logicalCh) {
  selectMuxChannel(MUX_CHANNEL_MAP[logicalCh]);
}

void disableMux() {
  Wire.beginTransmission(MUX_I2C_ADDR);
  Wire.write(0x00);
  Wire.endTransmission();
}

// =============================================================================
// CSV / string helpers
// =============================================================================
String trimCopy(String value) {
  value.trim();
  return value;
}

String getCsvPart(const String &line, int index) {
  int currentIndex = 0;
  int start = 0;
  for (int i = 0; i <= (int)line.length(); i++) {
    if (i == (int)line.length() || line.charAt(i) == ',') {
      if (currentIndex == index) {
        return trimCopy(line.substring(start, i));
      }
      currentIndex++;
      start = i + 1;
    }
  }
  return "";
}

// =============================================================================
// OLED drawing helpers
//   Caller must have already called selectMuxChannel(ch) before these.
// =============================================================================
void drawCenteredSmall(const String &text, int y) {
  int16_t  x1;
  int16_t  y1;
  uint16_t w;
  uint16_t h;
  display.getTextBounds(text, 0, y, &x1, &y1, &w, &h);
  int x = max(0, (SCREEN_WIDTH - (int)w) / 2);
  display.setCursor(x, y);
  display.print(text);
}

// =============================================================================
// Brightness / power (per-channel — each selects its own mux channel first)
// =============================================================================
int brightnessPercentToContrast(int percent) {
  percent = constrain(percent, 0, 100);
  if (percent <= 0) return 0;
  return max(1, (percent * percent * 255) / 10000);
}

void applyOledBrightnessToChannel(int ch, int percent) {
  if (!displayOk[ch]) return;
  selectDisplayChannel(ch);

  percent = constrain(percent, 0, 100);

  if (percent <= 0) {
    display.ssd1306_command(SSD1306_DISPLAYOFF);
    return;
  }

  display.ssd1306_command(SSD1306_DISPLAYON);

  int contrast  = brightnessPercentToContrast(percent);
  int precharge = map(percent, 1, 100, 0x11, 0xF1);
  int vcomh     = map(percent, 1, 100, 0x00, 0x40);

  display.ssd1306_command(SSD1306_SETCONTRAST);
  display.ssd1306_command(contrast);
  display.ssd1306_command(SSD1306_SETPRECHARGE);
  display.ssd1306_command(precharge);
  display.ssd1306_command(SSD1306_SETVCOMDETECT);
  display.ssd1306_command(vcomh);
}

void applyOledBrightnessToAll(int percent) {
  activeOledBrightnessPercent = constrain(percent, 0, 100);
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    applyOledBrightnessToChannel(ch, percent);
  }
}

void applyOledBrightness() {
  oledBrightnessPercent = constrain(oledBrightnessPercent, 0, 100);
  applyOledBrightnessToAll(oledBrightnessPercent);
}

void setAllDisplaysPower(bool on) {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    selectDisplayChannel(ch);
    display.ssd1306_command(on ? SSD1306_DISPLAYON : SSD1306_DISPLAYOFF);
  }
}

// =============================================================================
// Anti-burn-in pixel shift (same offset applied to all displays in sync)
//   Hardware SETDISPLAYOFFSET shifts the whole panel down 0..3px and WRAPS, so
//   every display mode must keep its content within rows 0..60 (see updateDisplay);
//   the reserved bottom 3 rows mean a full shift only ever wraps empty rows.
//   (A future firmware could replace this with a clamped 2-D software jitter — see
//   the "software-jitter anti-burn" item in the dashboard FEATURE_BACKLOG.)
// =============================================================================
void applyAntiBurnInOffset() {
  uint8_t offset = 0;
  if (antiBurnInEnabled && !displaySleeping) {
    offset = (uint8_t)((millis() / 30000UL) % 4);
  }
  if (offset == lastBurnInOffset) return;

  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    selectDisplayChannel(ch);
    display.ssd1306_command(SSD1306_SETDISPLAYOFFSET);
    display.ssd1306_command(offset);
  }
  lastBurnInOffset = offset;
}

// =============================================================================
// Display rendering (per-channel)
//   Mode resolution: channelMode[ch] overrides the global displayMode if set.
// =============================================================================
void updateDisplay(int ch) {
  if (ch < 0 || ch >= CHANNEL_COUNT) return;
  if (!displayOk[ch] || displaySleeping) return;
  // Hold the startup version splash for its full 2 s; state received meanwhile
  // is already stored and gets drawn by endBootSplash().
  if (bootSplashActive) return;

  applyAntiBurnInOffset();

  selectDisplayChannel(ch);
  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);

  // Resolve display mode: per-channel override if set, else global default.
  String mode = (channelMode[ch].length() > 0) ? channelMode[ch] : displayMode;
  mode.toUpperCase();

  const ChannelState &c = channels[ch];
  String volumeText  = String(c.volume) + "%";
  String muteText    = c.muted ? "Muted" : "Unmuted";
  String statusLine  = pcConnected ? c.status : "Disconnected";

  if (mode == "LARGE_VOLUME") {
    // Larger channel-name header (size 2), a rule, then a big value (size 4).
    // No separate mute strip: when muted, the big number becomes the word "MUTE".
    // Header at y3 (not y0) and rule at y20 so all content lives within rows 3..60,
    // leaving a top margin the 0..3px anti-burn shift can't wrap (v2.28).
    display.setTextSize(2);
    drawCenteredSmall(c.label, 3);
    display.drawLine(0, 20, SCREEN_WIDTH, 20, SSD1306_WHITE);
    String bigText = c.muted ? "MUTE" : volumeText;
    display.setTextSize(4);
    int16_t x1; int16_t y1; uint16_t w; uint16_t h;
    display.getTextBounds(bigText, 0, 0, &x1, &y1, &w, &h);
    display.setCursor((SCREEN_WIDTH - (int)w) / 2, 26);
    display.print(bigText);

  } else if (mode == "MUTE_STATUS") {
    display.setTextSize(2);
    drawCenteredSmall(c.muted ? "MUTED" : "ACTIVE", 12);
    display.setTextSize(1);
    drawCenteredSmall(c.label, 40);
    drawCenteredSmall(volumeText, 54);

  } else if (mode == "APP_OR_DEVICE_NAME") {
    display.setTextSize(1);
    drawCenteredSmall("CHANNEL " + String(ch + 1), 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    drawCenteredSmall(c.label, 24);
    drawCenteredSmall(statusLine, 40);
    drawCenteredSmall(volumeText, 54);

  } else if (mode == "BAR_PERCENT") {
    display.setTextSize(1);
    drawCenteredSmall(c.label, 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.drawRect(8, 28, 112, 14, SSD1306_WHITE);
    int barWidth = map(constrain(c.volume, 0, 100), 0, 100, 0, 108);
    display.fillRect(10, 30, barWidth, 10, SSD1306_WHITE);
    drawCenteredSmall(volumeText, 46);
    drawCenteredSmall(muteText, 54);

  } else {
    // Default: APP_VOLUME — label + large-ish volume + mute + status
    display.setTextSize(1);
    drawCenteredSmall(c.label, 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.setTextSize(2);
    int16_t x1; int16_t y1; uint16_t w; uint16_t h;
    display.getTextBounds(volumeText, 0, 0, &x1, &y1, &w, &h);
    display.setCursor((SCREEN_WIDTH - (int)w) / 2, 22);
    display.print(volumeText);
    display.setTextSize(1);
    drawCenteredSmall(muteText, 46);
    drawCenteredSmall(statusLine, 54);
  }

  display.display();
}

void updateAllDisplays() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    updateDisplay(ch);
  }
}

// =============================================================================
// Sleep / wake
// =============================================================================
void enterControllerSleep(const String &reason) {
  if (displaySleeping && sleepReason == reason) return;

  sleepReason           = reason;
  displaySleeping       = true;
  sleepBlocksFirstInput = true;
  setAllDisplaysPower(false);
  Serial.print("SLEEPING,");
  Serial.println(reason);
}

void wakeController(const String &reason) {
  markLocalActivity();
  if (!displaySleeping) {
    sleepBlocksFirstInput = false;
    return;
  }

  displaySleeping       = false;
  connectedIdleActive   = false;
  sleepBlocksFirstInput = false;
  applyOledBrightness();
  updateAllDisplays();
  Serial.print("AWAKE,");
  Serial.println(reason);
}

// =============================================================================
// PC connection state
// =============================================================================
void showDisconnected() {
  pcConnected = false;
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    channels[ch].label  = "Disconnected";
    channels[ch].volume = 0;
    channels[ch].muted  = true;
    channels[ch].status = "No PC";
  }
  lastLocalActivityMs  = millis();
  lastDisplayActivityMs = millis();
  connectedIdleActive  = false;
  applyOledBrightness();
  updateAllDisplays();
}

void markPcMessageReceived() {
  pcConnected    = true;
  lastPcMessageMs = millis();
}

// =============================================================================
// Serial debug
// =============================================================================
void sendDebug(const String &message) {
  Serial.print("DBG,");
  Serial.println(message);
}

// =============================================================================
// Identity
//   Sends HELLO with 5 comma-separated fields:
//     HELLO,<name>,<protocol>,<channels>,<chipId>
//   The chip ID (field 5) is derived from the eFuse MAC upper 32 bits and
//   formatted as a zero-padded 8-digit hex string (e.g. "0x1A2B3C4D").
//   The dashboard uses this to pair and verify the correct controller.
// =============================================================================
void sendHello() {
  uint32_t chipId = (uint32_t)(ESP.getEfuseMac() >> 32);
  char chipIdStr[12];
  snprintf(chipIdStr, sizeof(chipIdStr), "0x%08X", chipId);

  Serial.print("HELLO,");
  Serial.print(FIRMWARE_NAME);
  Serial.print(",");
  Serial.print(PROTOCOL_VERSION);
  Serial.print(",");
  Serial.print(CHANNEL_COUNT);
  Serial.print(",");
  Serial.println(chipIdStr);
}

// =============================================================================
// Activity tracking / connected idle
// =============================================================================
void exitConnectedIdle(const String &reason) {
  if (!connectedIdleActive) return;
  connectedIdleActive = false;
  applyOledBrightness();
  updateAllDisplays();
  Serial.print("OLED_IDLE_END,");
  Serial.println(reason);
}

void markDisplayActivity(const String &reason) {
  lastDisplayActivityMs = millis();
  exitConnectedIdle(reason);
}

void markLocalActivity() {
  lastLocalActivityMs = millis();
  markDisplayActivity("LOCAL");
}

// =============================================================================
// Display test pattern
// =============================================================================
void showDisplayTestPattern() {
  markLocalActivity();

  bool anyOk = false;
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) if (displayOk[ch]) { anyOk = true; break; }
  if (!anyOk) {
    Serial.println("ERR,DISPLAY_NOT_AVAILABLE");
    return;
  }

  displaySleeping       = false;
  sleepBlocksFirstInput = false;

  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    applyOledBrightnessToChannel(ch, oledBrightnessPercent);
    selectDisplayChannel(ch);
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(1);
    drawCenteredSmall("DISPLAY TEST", 0);
    display.drawRect(0, 12, SCREEN_WIDTH, 40, SSD1306_WHITE);
    display.drawLine(0, 12, SCREEN_WIDTH - 1, 51, SSD1306_WHITE);
    display.drawLine(SCREEN_WIDTH - 1, 12, 0, 51, SSD1306_WHITE);
    drawCenteredSmall("Ch" + String(ch + 1) + "  v" PROTOCOL_VERSION, 56);
    display.display();
  }

  sendDebug("Display test shown on " + String(CHANNEL_COUNT) + " OLEDs");
}

// =============================================================================
// Serial message handlers
// =============================================================================
void handleStateMessage(const String &line) {
  // STATE,<channelIndex>,<label>,<volume>,<muted>
  // Updates the specified channel's state and refreshes its OLED.
  int channelIndex = getCsvPart(line, 1).toInt();
  if (channelIndex < 0 || channelIndex >= CHANNEL_COUNT) return;

  String label     = getCsvPart(line, 2);
  int    newVolume = constrain(getCsvPart(line, 3).toInt(), 0, 100);
  bool   newMuted  = getCsvPart(line, 4).toInt() != 0;

  if (label.length() == 0) label = "Unknown";

  bool changed = label     != channels[channelIndex].label  ||
                 newVolume != channels[channelIndex].volume  ||
                 newMuted  != channels[channelIndex].muted;

  channels[channelIndex].label  = label;
  channels[channelIndex].volume = newVolume;
  channels[channelIndex].muted  = newMuted;
  channels[channelIndex].status = "Active";

  if (changed) markDisplayActivity("PC_STATE");
  updateDisplay(channelIndex);
}

void handleChannelStateMessage(const String &line) {
  // CHSTATE,<channelIndex>,<label>,<volume>,<muted>,<status>
  int channelIndex = getCsvPart(line, 1).toInt();
  if (channelIndex < 0 || channelIndex >= CHANNEL_COUNT) return;

  String label     = getCsvPart(line, 2);
  int    newVolume = constrain(getCsvPart(line, 3).toInt(), 0, 100);
  bool   newMuted  = getCsvPart(line, 4).toInt() != 0;
  String status    = getCsvPart(line, 5);

  String newLabel  = label.length()  > 0 ? label  : "Unknown";
  String newStatus = status.length() > 0 ? status : "Unknown";

  bool changed = newLabel  != channels[channelIndex].label  ||
                 newVolume != channels[channelIndex].volume  ||
                 newMuted  != channels[channelIndex].muted   ||
                 newStatus != channels[channelIndex].status;

  channels[channelIndex].label  = newLabel;
  channels[channelIndex].volume = newVolume;
  channels[channelIndex].muted  = newMuted;
  channels[channelIndex].status = newStatus;

  if (changed) markDisplayActivity("PC_CHSTATE");
  updateDisplay(channelIndex);
}

void handleOledConfigMessage(const String &line) {
  // OLEDCFG,<mode>,<brightness>,<disconnectedTimeout>,<idleAction>,<idleTimeout>,<antiBurnIn>
  String mode               = getCsvPart(line, 1);
  String brightnessText     = getCsvPart(line, 2);
  String disconnTimeoutText = getCsvPart(line, 3);
  String idleActionText     = getCsvPart(line, 4);
  String idleTimeoutText    = getCsvPart(line, 5);
  String antiBurnInText     = getCsvPart(line, 6);

  if (mode.length() == 0) mode = "AppVolume";
  displayMode          = mode;
  oledBrightnessPercent = constrain(brightnessText.toInt(), 0, 100);

  int disconnectedTimeoutMinutes = constrain(disconnTimeoutText.toInt(), 1, 60);
  noDashboardSleepMs = (unsigned long)disconnectedTimeoutMinutes * 60000UL;

  idleActionText.toUpperCase();
  if (idleActionText == "DIM_10" || idleActionText == "DIM_20" ||
      idleActionText == "DIM_30" || idleActionText == "DIM_40" ||
      idleActionText == "DIM_50" || idleActionText == "DIM_60" ||
      idleActionText == "DIM_70") {
    connectedIdleAction = idleActionText;
  } else {
    connectedIdleAction = "OFF";
  }

  int idleTimeoutMinutes = constrain(
    idleTimeoutText.toInt() <= 0 ? 10 : idleTimeoutText.toInt(), 1, 60);
  connectedIdleTimeoutMs = (unsigned long)idleTimeoutMinutes * 60000UL;
  antiBurnInEnabled = antiBurnInText.length() == 0 ? true : antiBurnInText.toInt() != 0;

  connectedIdleActive = false;
  applyOledBrightness();
  markDisplayActivity("OLEDCFG");
  updateAllDisplays();

  // ACK with resolved values
  Serial.print("OLEDCFG_ACK,");
  Serial.print(displayMode);
  Serial.print(",");
  Serial.print(oledBrightnessPercent);
  Serial.print(",");
  Serial.print(disconnectedTimeoutMinutes);
  Serial.print(",");
  Serial.print(connectedIdleAction);
  Serial.print(",");
  Serial.print(idleTimeoutMinutes);
  Serial.print(",");
  Serial.println(antiBurnInEnabled ? 1 : 0);
}

void handleDispModeMessage(const String &line) {
  // DISPMODE,<channelIndex>,<mode>
  //   Sets the per-channel OLED display mode override.
  //   Empty <mode> clears the override so the channel uses the global OLEDCFG mode.
  int channelIndex = getCsvPart(line, 1).toInt();
  if (channelIndex < 0 || channelIndex >= CHANNEL_COUNT) return;

  channelMode[channelIndex] = getCsvPart(line, 2);   // "" = use global default

  markDisplayActivity("DISPMODE");
  updateDisplay(channelIndex);

  sendDebug("Ch" + String(channelIndex) + " display mode: " +
    (channelMode[channelIndex].length() > 0 ? channelMode[channelIndex] : "global"));
}

// =============================================================================
// Serial I/O
// =============================================================================
void handleSerialLine(String line) {
  line.trim();
  if (line.length() == 0) return;

  markPcMessageReceived();

  if (line == "PING") {
    Serial.println("PONG");
    return;
  }
  if (line == "HELLO?") {
    sendHello();
    return;
  }
  if (line == "TEST_DISPLAY") {
    showDisplayTestPattern();
    return;
  }
  if (line == "SHOW_IDENT") {
    showOledIdentScreen();
    return;
  }
  if (line == "SCAN_I2C") {
    scanI2cBus();
    return;
  }
  if (line == "DISCONNECT") {
    showDisconnected();
    return;
  }
  if (line.startsWith("SLEEP")) {
    String reason = getCsvPart(line, 1);
    if (reason.length() == 0) reason = "PC";
    enterControllerSleep(reason);
    return;
  }
  if (line.startsWith("WAKE")) {
    String reason = getCsvPart(line, 1);
    if (reason.length() == 0) reason = "PC_ACTIVE";
    wakeController(reason);
    return;
  }
  if (line.startsWith("OLEDCFG,")) {
    handleOledConfigMessage(line);
    return;
  }
  if (line.startsWith("DISPMODE,")) {
    handleDispModeMessage(line);
    return;
  }
  if (line.startsWith("STATE,")) {
    handleStateMessage(line);
    return;
  }
  if (line.startsWith("CHSTATE,")) {
    handleChannelStateMessage(line);
    return;
  }
}

void readSerialMessages() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      String line = serialInputBuffer;
      serialInputBuffer = "";
      handleSerialLine(line);
      continue;
    }
    if (serialInputBuffer.length() < 120) {
      serialInputBuffer += c;
    } else {
      serialInputBuffer = "";
      sendDebug("Serial input line too long; buffer cleared");
    }
  }
}

// =============================================================================
// Encoder reading (per-channel, quadrature decode — DEBOUNCE DISABLED)
//
// All four software debounce layers have been removed for diagnostic purposes:
//   1. ENC_DEBOUNCE_US=0  — 1 µs transition gate bypassed
//   2. invalid-quadrature accumulator reset removed — skip only, no reset
//   3. direction-reversal reset removed — always accumulate
//   4. ENC_REPORT_GUARD_MS=0 — 3 ms report rate-limit bypassed
// =============================================================================
uint8_t readEncoderRawState(int ch) {
  uint8_t a = digitalRead(ENC_A_PIN[ch]) == LOW ? 0 : 1;
  uint8_t b = digitalRead(ENC_B_PIN[ch]) == LOW ? 0 : 1;
  return (a << 1) | b;
}

void readEncoder(int ch) {
  static const int8_t transitionTable[16] = {
     0, -1,  1,  0,
     1,  0,  0, -1,
    -1,  0,  0,  1,
     0,  1, -1,  0
  };

  uint8_t       currentState = readEncoderRawState(ch);
  EncoderState &enc          = encoders[ch];

  if (currentState == enc.lastState) return;

  if (displaySleeping) {
    enc.lastState   = currentState;
    enc.accumulator = 0;
    wakeController("ENCODER");
    return;
  }

  markLocalActivity();

  unsigned long nowUs = micros();
  // DIAGNOSTIC: ENC_DEBOUNCE_US=0 — gate condition is never true; transition always accepted.
  if (nowUs - enc.lastTransitionUs < ENC_DEBOUNCE_US) return;

  uint8_t transitionIndex = (enc.lastState << 2) | currentState;
  int8_t  movement        = transitionTable[transitionIndex];

  enc.lastState        = currentState;
  enc.lastTransitionUs = nowUs;

  // DIAGNOSTIC: invalid-quadrature reset removed — skip the transition but keep accumulator.
  if (movement == 0) return;

  // DIAGNOSTIC: direction-reversal reset removed — always accumulate.
  enc.accumulator += movement;

  unsigned long nowMs = millis();
  // DIAGNOSTIC: ENC_REPORT_GUARD_MS=0 — rate-limit condition is always true; report immediately.
  if (enc.accumulator >= ENC_COUNTS_PER_DETENT && nowMs - enc.lastReportMs >= ENC_REPORT_GUARD_MS) {
    Serial.print("ENC,");
    Serial.print(ch);
    Serial.println(",1");
    enc.accumulator  = 0;
    enc.lastReportMs = nowMs;
  } else if (enc.accumulator <= -ENC_COUNTS_PER_DETENT && nowMs - enc.lastReportMs >= ENC_REPORT_GUARD_MS) {
    Serial.print("ENC,");
    Serial.print(ch);
    Serial.println(",-1");
    enc.accumulator  = 0;
    enc.lastReportMs = nowMs;
  }
}

void readAllEncoders() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    readEncoder(ch);
  }
}

// =============================================================================
// Button reading (per-channel, debounced, short/long press)
// Note: BTN_DEBOUNCE_MS is intentionally kept — button debounce is separate
// from encoder debounce and is needed for correct press detection.
// =============================================================================
void readButton(int ch) {
  bool          currentState = digitalRead(ENC_SW_PIN[ch]);
  unsigned long now          = millis();
  ButtonState  &btn          = buttons[ch];

  if (currentState == btn.lastState || now - btn.lastChangeMs <= BTN_DEBOUNCE_MS) return;

  btn.lastChangeMs = now;
  btn.lastState    = currentState;
  markLocalActivity();

  if (displaySleeping && currentState == LOW) {
    btn.pressed = false;
    wakeController("BUTTON");
    return;
  }

  if (currentState == LOW) {
    btn.pressed = true;
    btn.downMs  = now;
  } else if (btn.pressed) {
    unsigned long pressDuration = now - btn.downMs;
    if (pressDuration >= BTN_LONG_MS) {
      Serial.print("BTN_LONG,");
      Serial.println(ch);
    } else {
      // Short press — fire immediately (no delay), then check for double press
      Serial.print("BTN_SHORT,");
      Serial.println(ch);
      if (btn.lastShortPressMs > 0 && now - btn.lastShortPressMs <= BTN_DOUBLE_MS) {
        Serial.print("BTN_DOUBLE,");
        Serial.println(ch);
        btn.lastShortPressMs = 0;  // reset so a third press does not re-trigger
      } else {
        btn.lastShortPressMs = now;
      }
    }
    btn.pressed = false;
  }
}

void readAllButtons() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    readButton(ch);
  }
}

// =============================================================================
// Periodic checks
// =============================================================================
void checkConnectedDisplayIdle() {
  if (displaySleeping || !pcConnected) return;

  bool anyOk = false;
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) if (displayOk[ch]) { anyOk = true; break; }
  if (!anyOk) return;

  unsigned long now = millis();
  if (!connectedIdleActive && now - lastDisplayActivityMs >= connectedIdleTimeoutMs) {
    connectedIdleActive = true;
    if (connectedIdleAction.startsWith("DIM_")) {
      int idleDimPercent = constrain(connectedIdleAction.substring(4).toInt(), 10, 70);
      int dimTo = min(oledBrightnessPercent, idleDimPercent);
      applyOledBrightnessToAll(dimTo);
      updateAllDisplays();
    } else {
      setAllDisplaysPower(false);
    }
    Serial.print("OLED_IDLE_START,");
    Serial.println(connectedIdleAction);
  }
}

void checkPcTimeout() {
  unsigned long now = millis();
  if (pcConnected && now - lastPcMessageMs > PC_TIMEOUT_MS) {
    showDisconnected();
  }
  if (!pcConnected && !displaySleeping && now - lastLocalActivityMs > noDashboardSleepMs) {
    enterControllerSleep("DASHBOARD_DISCONNECTED");
  }
}

// =============================================================================
// I2C bus scan
//   Scans the root bus (mux disabled) then each of the CHANNEL_COUNT mux
//   channels. All results are sent as DBG messages so they appear in the
//   dashboard debug console. Known device addresses are annotated by name.
//   Call scanI2cBus() directly or send SCAN_I2C over serial at any time.
// =============================================================================
String i2cDeviceName(uint8_t addr) {
  if (addr == MUX_I2C_ADDR) return "TCA9548A mux";
  if (addr == OLED_I2C_ADDR) return "OLED (SSD1315)";
  if (addr == 0x3D)          return "OLED alt addr";
  return "unknown";
}

void scanI2cBus() {
  int totalFound = 0;

  // ── Phase 1: root bus with mux disabled ──────────────────────────────────
  disableMux();
  delay(5);

  sendDebug("I2C scan start — root bus");
  int rootFound = 0;
  for (uint8_t addr = 1; addr < 127; addr++) {
    Wire.beginTransmission(addr);
    if (Wire.endTransmission() == 0) {
      sendDebug("  root  0x" + String(addr, HEX) + "  " + i2cDeviceName(addr));
      rootFound++;
    }
  }
  sendDebug("  root: " + String(rootFound) + " device(s)");
  totalFound += rootFound;

  // ── Phase 2: each mux channel ────────────────────────────────────────────
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    selectMuxChannel(ch);
    delay(5);

    int chFound = 0;
    for (uint8_t addr = 1; addr < 127; addr++) {
      if (addr == MUX_I2C_ADDR) continue;   // mux always visible; skip to avoid noise
      Wire.beginTransmission(addr);
      if (Wire.endTransmission() == 0) {
        sendDebug("  mux ch" + String(ch) + "  0x" + String(addr, HEX) + "  " + i2cDeviceName(addr));
        chFound++;
      }
    }
    if (chFound == 0) {
      sendDebug("  mux ch" + String(ch) + "  no devices");
    }
    totalFound += chFound;
  }

  disableMux();
  sendDebug("I2C scan complete — " + String(totalFound) + " device(s) found");
}

// =============================================================================
// Default channel state
// =============================================================================
void setupDefaultChannelStates() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    channels[ch].label  = "Unassigned";
    channels[ch].volume = 0;
    channels[ch].muted  = true;
    channels[ch].status = "Waiting";
    channelMode[ch]     = "";   // inherit global mode by default
  }
  channels[0].label = "Master";
}

// =============================================================================
// OLED identification screen
//   Shows "OLED-N" in large text on each display so you can confirm that
//   mux channel N is wired to the correct physical display.
//   Triggered via the dashboard Hardware Test tab (SHOW_IDENT serial command).
// =============================================================================
void showOledIdentScreen() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    applyOledBrightnessToChannel(ch, oledBrightnessPercent);
    selectDisplayChannel(ch);
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);

    // Large channel label centred vertically
    String label = "OLED-" + String(ch + 1);
    display.setTextSize(3);
    int16_t x1, y1; uint16_t w, h;
    display.getTextBounds(label, 0, 0, &x1, &y1, &w, &h);
    display.setCursor((SCREEN_WIDTH - (int)w) / 2, (SCREEN_HEIGHT - (int)h) / 2 - 6);
    display.print(label);

    // Small firmware version at the bottom
    display.setTextSize(1);
    drawCenteredSmall("v" PROTOCOL_VERSION, 54);

    display.display();
  }

  delay(2000);
}

// =============================================================================
// Startup splashes
//   showFirmwareVersionSplash — big centred firmware version on every OLED,
//     shown for BOOT_SPLASH_MS at boot so the flashed version is easy to confirm.
//   showWaitingSplash — the per-channel "Waiting for PC" screen shown once the
//     version splash ends (and originally the only boot screen).
//   endBootSplash — clears the version splash and draws whatever should be on
//     screen now: live channel state if the PC is already connected, else the
//     waiting splash.
// =============================================================================
void showFirmwareVersionSplash() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    applyOledBrightnessToChannel(ch, oledBrightnessPercent);
    selectDisplayChannel(ch);
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(2);
    drawCenteredSmall("Firmware", 6);
    display.setTextSize(3);
    drawCenteredSmall("v" PROTOCOL_VERSION, 34);
    display.display();
  }
}

void showWaitingSplash() {
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    if (!displayOk[ch]) continue;
    applyOledBrightnessToChannel(ch, oledBrightnessPercent);
    selectDisplayChannel(ch);
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(1);
    drawCenteredSmall("PC Volume", 8);
    drawCenteredSmall("Controller", 22);
    drawCenteredSmall("Ch " + String(ch + 1) + " of " + String(CHANNEL_COUNT), 38);
    drawCenteredSmall("Waiting for PC", 52);
    display.display();
  }
}

void endBootSplash() {
  bootSplashActive = false;
  if (pcConnected) {
    updateAllDisplays();   // draw the state the PC pushed while the splash held
  } else {
    showWaitingSplash();
  }
}

// =============================================================================
// Setup
// =============================================================================
void setup() {
  // Configure encoder pins with internal pull-ups (no external resistors needed)
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    pinMode(ENC_A_PIN[ch],  INPUT_PULLUP);
    pinMode(ENC_B_PIN[ch],  INPUT_PULLUP);
    pinMode(ENC_SW_PIN[ch], INPUT_PULLUP);
  }

  Serial.begin(115200);
  delay(300);

  Wire.begin(PIN_I2C_SDA, PIN_I2C_SCL);

  // Optional: scan I2C bus and report to dashboard debug log on startup
  // (controlled by I2C_SCAN_ON_STARTUP near the top of the file)
#if I2C_SCAN_ON_STARTUP
  scanI2cBus();
#endif

  // Initialise each OLED through the mux.
  // Adafruit_SSD1306::begin() allocates the framebuffer on the first call;
  // subsequent calls reuse the existing buffer and re-run the init sequence
  // on whichever display the mux is currently pointing at.
  int okCount = 0;
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    displayOk[ch] = false;
    selectDisplayChannel(ch);
    displayOk[ch] = display.begin(SSD1306_SWITCHCAPVCC, OLED_I2C_ADDR);
    if (displayOk[ch]) {
      okCount++;
    } else {
      sendDebug("OLED ch" + String(ch) + " init failed");
    }
  }

  if (okCount == 0) {
    sendDebug("No OLEDs initialised; display unavailable");
  }

  setupDefaultChannelStates();

  // Show the firmware-version splash first (held for BOOT_SPLASH_MS, non-blocking
  // — loop() ends it and falls through to the "Waiting for PC" screen).
  showFirmwareVersionSplash();
  bootSplashActive  = true;
  bootSplashUntilMs = millis() + BOOT_SPLASH_MS;

  // Initialise encoder / button state arrays
  unsigned long nowUs = micros();
  unsigned long nowMs = millis();
  for (int ch = 0; ch < CHANNEL_COUNT; ch++) {
    encoders[ch] = { readEncoderRawState(ch), 0, nowUs, nowMs };
    buttons[ch]  = { HIGH, false, 0, 0, 0 };
  }

  lastLocalActivityMs  = millis();
  lastDisplayActivityMs = millis();

  sendHello();
}

// =============================================================================
// Main loop
// =============================================================================
void loop() {
  readSerialMessages();
  if (bootSplashActive && (long)(millis() - bootSplashUntilMs) >= 0) {
    endBootSplash();
  }
  readAllEncoders();
  readAllButtons();
  checkConnectedDisplayIdle();
  checkPcTimeout();
}
