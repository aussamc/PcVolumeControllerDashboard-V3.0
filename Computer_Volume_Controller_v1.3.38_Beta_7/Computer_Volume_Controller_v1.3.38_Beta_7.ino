#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>

// =====================
// Firmware identity
// =====================
#define FIRMWARE_NAME "PC_VOLUME_CONTROLLER"
#define PROTOCOL_VERSION "1.3.38 Beta 7"
#define LOGICAL_CHANNEL_COUNT 6

// =====================
// Prototype hardware pins
// Updated to use #define definitions
// =====================
#define PIN_ENC_CLK 2
#define PIN_ENC_DT  3
#define PIN_ENC_SW  4

#define PIN_I2C_SDA 9
#define PIN_I2C_SCL 10

// =====================
// OLED
// =====================
#define SCREEN_WIDTH 128
#define SCREEN_HEIGHT 64
#define OLED_RESET -1
#define OLED_I2C_ADDRESS 0x3C

Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET);

bool displayOk = false;
bool displaySleeping = false;
bool sleepBlocksFirstInput = false;
String sleepReason = "";
String serialInputBuffer = "";

// =====================
// Encoder/button state
// =====================
uint8_t lastEncoderState = 0;
int8_t encoderAccumulator = 0;
unsigned long lastEncoderTransitionUs = 0;
unsigned long lastEncoderReportMs = 0;
const unsigned long encoderTransitionDebounceUs = 1000;
const unsigned long encoderReportGuardMs = 3;
const int8_t encoderCountsPerDetent = 4;

bool lastButtonState = HIGH;
bool buttonPressed = false;
unsigned long buttonDownMs = 0;
unsigned long lastButtonChangeMs = 0;
const unsigned long buttonDebounceMs = 50;
const unsigned long longPressMs = 650;

// =====================
// PC connection/watchdog
// =====================
bool pcConnected = false;
unsigned long lastPcMessageMs = 0;
const unsigned long pcTimeoutMs = 3000;
unsigned long lastLocalActivityMs = 0;
unsigned long noDashboardSleepMs = 120000;

// =====================
// Selected display state
// =====================
String currentLabel = "Disconnected";
int currentVolume = 0;
bool currentMuted = true;
String currentStatus = "No PC";
String displayMode = "AppVolume";
int oledBrightnessPercent = 80;
int activeOledBrightnessPercent = 80;
bool connectedIdleActive = false;
String connectedIdleAction = "DIM_30";
unsigned long connectedIdleTimeoutMs = 600000UL;
unsigned long lastDisplayActivityMs = 0;
bool antiBurnInEnabled = true;
uint8_t lastBurnInOffset = 255;

void updateDisplay();
void applyAntiBurnInOffset();
void markLocalActivity();
void markDisplayActivity(const String &reason);

// Future 6-channel storage.
// Current prototype only displays the selected/current STATE.
struct ChannelState {
  String label;
  int volume;
  bool muted;
  String status;
};

ChannelState channels[LOGICAL_CHANNEL_COUNT];

// =====================
// Helpers
// =====================
String trimCopy(String value) {
  value.trim();
  return value;
}

String getCsvPart(const String &line, int index) {
  int currentIndex = 0;
  int start = 0;

  for (int i = 0; i <= line.length(); i++) {
    if (i == line.length() || line.charAt(i) == ',') {
      if (currentIndex == index) {
        return trimCopy(line.substring(start, i));
      }

      currentIndex++;
      start = i + 1;
    }
  }

  return "";
}

void drawCenteredSmall(const String &text, int y) {
  int16_t x1;
  int16_t y1;
  uint16_t w;
  uint16_t h;

  display.getTextBounds(text, 0, y, &x1, &y1, &w, &h);
  int x = max(0, (SCREEN_WIDTH - (int)w) / 2);
  display.setCursor(x, y);
  display.print(text);
}

void updateDisplay() {
  if (!displayOk || displaySleeping) {
    return;
  }

  applyAntiBurnInOffset();
  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);

  String mode = displayMode;
  mode.toUpperCase();
  String label = currentLabel;
  String volumeText = String(currentVolume) + "%";
  String muteText = currentMuted ? "Muted" : "Unmuted";
  String statusLine = pcConnected ? currentStatus : "Disconnected";

  if (mode == "LARGE_VOLUME") {
    display.setTextSize(1);
    drawCenteredSmall(label, 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.setTextSize(3);
    int16_t x1;
    int16_t y1;
    uint16_t w;
    uint16_t h;
    display.getTextBounds(volumeText, 0, 0, &x1, &y1, &w, &h);
    display.setCursor((SCREEN_WIDTH - (int)w) / 2, 24);
    display.print(volumeText);
    display.setTextSize(1);
    drawCenteredSmall(muteText, 56);
  } else if (mode == "MUTE_STATUS") {
    display.setTextSize(2);
    drawCenteredSmall(currentMuted ? "MUTED" : "ACTIVE", 12);
    display.setTextSize(1);
    drawCenteredSmall(label, 40);
    drawCenteredSmall(volumeText, 54);
  } else if (mode == "APP_OR_DEVICE_NAME") {
    display.setTextSize(1);
    drawCenteredSmall("CHANNEL", 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.setTextSize(1);
    drawCenteredSmall(label, 24);
    drawCenteredSmall(statusLine, 40);
    drawCenteredSmall(volumeText, 54);
  } else if (mode == "BAR_PERCENT") {
    display.setTextSize(1);
    drawCenteredSmall(label, 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.drawRect(8, 28, 112, 14, SSD1306_WHITE);
    int barWidth = map(constrain(currentVolume, 0, 100), 0, 100, 0, 108);
    display.fillRect(10, 30, barWidth, 10, SSD1306_WHITE);
    drawCenteredSmall(volumeText, 48);
    drawCenteredSmall(muteText, 56);
  } else {
    display.setTextSize(1);
    drawCenteredSmall(label, 0);
    display.drawLine(0, 12, SCREEN_WIDTH, 12, SSD1306_WHITE);
    display.setTextSize(2);
    int16_t x1;
    int16_t y1;
    uint16_t w;
    uint16_t h;
    display.getTextBounds(volumeText, 0, 0, &x1, &y1, &w, &h);
    display.setCursor((SCREEN_WIDTH - (int)w) / 2, 22);
    display.print(volumeText);
    display.setTextSize(1);
    drawCenteredSmall(muteText, 46);
    drawCenteredSmall(statusLine, 56);
  }

  display.display();
}


void setDisplayPower(bool on) {
  if (!displayOk) {
    return;
  }

  display.ssd1306_command(on ? SSD1306_DISPLAYON : SSD1306_DISPLAYOFF);
}

int brightnessPercentToContrast(int percent) {
  percent = constrain(percent, 0, 100);
  if (percent <= 0) {
    return 0;
  }
  return max(1, (percent * percent * 255) / 10000);
}

void applyOledBrightnessPercent(int percent) {
  if (!displayOk) {
    return;
  }

  activeOledBrightnessPercent = constrain(percent, 0, 100);

  if (activeOledBrightnessPercent <= 0) {
    setDisplayPower(false);
    return;
  }

  setDisplayPower(true);

  int contrast = brightnessPercentToContrast(activeOledBrightnessPercent);
  int precharge = map(activeOledBrightnessPercent, 1, 100, 0x11, 0xF1);
  int vcomh = map(activeOledBrightnessPercent, 1, 100, 0x00, 0x40);

  display.ssd1306_command(SSD1306_SETCONTRAST);
  display.ssd1306_command(contrast);
  display.ssd1306_command(SSD1306_SETPRECHARGE);
  display.ssd1306_command(precharge);
  display.ssd1306_command(SSD1306_SETVCOMDETECT);
  display.ssd1306_command(vcomh);
}

void applyOledBrightness() {
  oledBrightnessPercent = constrain(oledBrightnessPercent, 0, 100);
  applyOledBrightnessPercent(oledBrightnessPercent);
}

void applyAntiBurnInOffset() {
  if (!displayOk) {
    return;
  }

  uint8_t offset = 0;
  if (antiBurnInEnabled && !displaySleeping) {
    offset = (millis() / 30000UL) % 4;
  }

  if (offset != lastBurnInOffset) {
    display.ssd1306_command(SSD1306_SETDISPLAYOFFSET);
    display.ssd1306_command(offset);
    lastBurnInOffset = offset;
  }
}

void enterControllerSleep(const String &reason) {
  if (displaySleeping && sleepReason == reason) {
    return;
  }

  sleepReason = reason;
  displaySleeping = true;
  sleepBlocksFirstInput = true;
  setDisplayPower(false);
  Serial.print("SLEEPING,");
  Serial.println(reason);
}

void wakeController(const String &reason) {
  markLocalActivity();
  if (!displaySleeping) {
    sleepBlocksFirstInput = false;
    return;
  }

  displaySleeping = false;
  connectedIdleActive = false;
  sleepBlocksFirstInput = false;
  applyOledBrightness();
  updateDisplay();
  Serial.print("AWAKE,");
  Serial.println(reason);
}

void showDisconnected() {
  pcConnected = false;
  currentLabel = "Disconnected";
  currentVolume = 0;
  currentMuted = true;
  currentStatus = "No PC";
  lastLocalActivityMs = millis();
  lastDisplayActivityMs = millis();
  connectedIdleActive = false;
  applyOledBrightness();
  updateDisplay();
}

void sendDebug(const String &message) {
  Serial.print("DBG,");
  Serial.println(message);
}

void sendHello() {
  Serial.print("HELLO,");
  Serial.print(FIRMWARE_NAME);
  Serial.print(",");
  Serial.print(PROTOCOL_VERSION);
  Serial.print(",");
  Serial.println(LOGICAL_CHANNEL_COUNT);
}

void exitConnectedIdle(const String &reason) {
  if (!connectedIdleActive) {
    return;
  }

  connectedIdleActive = false;
  applyOledBrightness();
  updateDisplay();
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

void markPcMessageReceived() {
  pcConnected = true;
  lastPcMessageMs = millis();
}

void handleStateMessage(const String &line) {
  // STATE,<displayIndex>,<label>,<volume>,<mute>
  String label = getCsvPart(line, 2);
  String volumeText = getCsvPart(line, 3);
  String muteText = getCsvPart(line, 4);

  if (label.length() == 0) {
    label = "Unknown";
  }

  int newVolume = constrain(volumeText.toInt(), 0, 100);
  bool newMuted = muteText.toInt() != 0;
  bool changed = label != currentLabel || newVolume != currentVolume || newMuted != currentMuted;

  currentLabel = label;
  currentVolume = newVolume;
  currentMuted = newMuted;
  currentStatus = "Active";

  if (changed) {
    markDisplayActivity("PC_STATE");
  }

  updateDisplay();
}

void handleChannelStateMessage(const String &line) {
  // CHSTATE,<channelIndex>,<label>,<volume>,<mute>,<status>
  int channelIndex = getCsvPart(line, 1).toInt();

  if (channelIndex < 0 || channelIndex >= LOGICAL_CHANNEL_COUNT) {
    return;
  }

  String label = getCsvPart(line, 2);
  String volumeText = getCsvPart(line, 3);
  String muteText = getCsvPart(line, 4);
  String status = getCsvPart(line, 5);

  String newLabel = label.length() > 0 ? label : "Unknown";
  int newVolume = constrain(volumeText.toInt(), 0, 100);
  bool newMuted = muteText.toInt() != 0;
  String newStatus = status.length() > 0 ? status : "Unknown";

  bool changed = newLabel != channels[channelIndex].label ||
                 newVolume != channels[channelIndex].volume ||
                 newMuted != channels[channelIndex].muted ||
                 newStatus != channels[channelIndex].status;

  channels[channelIndex].label = newLabel;
  channels[channelIndex].volume = newVolume;
  channels[channelIndex].muted = newMuted;
  channels[channelIndex].status = newStatus;

  if (changed) {
    markDisplayActivity("PC_CHSTATE");
  }
}

void handleOledConfigMessage(const String &line) {
  String mode = getCsvPart(line, 1);
  String brightnessText = getCsvPart(line, 2);
  String disconnectedTimeoutText = getCsvPart(line, 3);
  String connectedIdleActionText = getCsvPart(line, 4);
  String connectedIdleTimeoutText = getCsvPart(line, 5);
  String antiBurnInText = getCsvPart(line, 6);

  if (mode.length() == 0) {
    mode = "AppVolume";
  }

  displayMode = mode;
  oledBrightnessPercent = constrain(brightnessText.toInt(), 0, 100);

  int disconnectedTimeoutMinutes = constrain(disconnectedTimeoutText.toInt(), 1, 60);
  noDashboardSleepMs = (unsigned long)disconnectedTimeoutMinutes * 60000UL;

  connectedIdleActionText.toUpperCase();
  if (connectedIdleActionText == "DIM_10" ||
      connectedIdleActionText == "DIM_20" ||
      connectedIdleActionText == "DIM_30" ||
      connectedIdleActionText == "DIM_40" ||
      connectedIdleActionText == "DIM_50" ||
      connectedIdleActionText == "DIM_60" ||
      connectedIdleActionText == "DIM_70") {
    connectedIdleAction = connectedIdleActionText;
  } else {
    connectedIdleAction = "OFF";
  }

  int connectedIdleTimeoutMinutes = constrain(connectedIdleTimeoutText.toInt() <= 0 ? 10 : connectedIdleTimeoutText.toInt(), 1, 60);
  connectedIdleTimeoutMs = (unsigned long)connectedIdleTimeoutMinutes * 60000UL;
  antiBurnInEnabled = antiBurnInText.length() == 0 ? true : antiBurnInText.toInt() != 0;

  connectedIdleActive = false;
  applyOledBrightness();
  markDisplayActivity("OLEDCFG");
  updateDisplay();

  Serial.print("OLEDCFG_ACK,");
  Serial.print(displayMode);
  Serial.print(",");
  Serial.print(oledBrightnessPercent);
  Serial.print(",");
  Serial.print(disconnectedTimeoutMinutes);
  Serial.print(",");
  Serial.print(connectedIdleAction);
  Serial.print(",");
  Serial.print(connectedIdleTimeoutMinutes);
  Serial.print(",");
  Serial.println(antiBurnInEnabled ? 1 : 0);
}

void showDisplayTestPattern() {
  markLocalActivity();
  if (!displayOk) {
    Serial.println("ERR,DISPLAY_NOT_AVAILABLE");
    return;
  }

  displaySleeping = false;
  sleepBlocksFirstInput = false;
  setDisplayPower(true);
  applyAntiBurnInOffset();
  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);
  display.setTextSize(1);
  drawCenteredSmall("DISPLAY TEST", 0);
  display.drawRect(0, 12, SCREEN_WIDTH, 40, SSD1306_WHITE);
  display.drawLine(0, 12, SCREEN_WIDTH - 1, 51, SSD1306_WHITE);
  display.drawLine(SCREEN_WIDTH - 1, 12, 0, 51, SSD1306_WHITE);
  drawCenteredSmall("v" PROTOCOL_VERSION, 56);
  display.display();
  Serial.println("DBG,Display test pattern shown");
}

void handleSerialLine(String line) {
  line.trim();

  if (line.length() == 0) {
    return;
  }

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

  if (line == "DISCONNECT") {
    showDisconnected();
    return;
  }

  if (line.startsWith("SLEEP")) {
    String reason = getCsvPart(line, 1);
    if (reason.length() == 0) {
      reason = "PC";
    }
    enterControllerSleep(reason);
    return;
  }

  if (line.startsWith("WAKE")) {
    String reason = getCsvPart(line, 1);
    if (reason.length() == 0) {
      reason = "PC_ACTIVE";
    }
    wakeController(reason);
    return;
  }

  if (line.startsWith("OLEDCFG,")) {
    handleOledConfigMessage(line);
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

    if (c == '\r') {
      continue;
    }

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

uint8_t readEncoderState() {
  uint8_t a = digitalRead(PIN_ENC_CLK) == LOW ? 0 : 1;
  uint8_t b = digitalRead(PIN_ENC_DT) == LOW ? 0 : 1;
  return (a << 1) | b;
}

void sendEncoderDelta(int delta) {
  Serial.print("ENC,0,");
  Serial.println(delta);
}

void readEncoder() {
  static const int8_t transitionTable[16] = {
     0, -1,  1,  0,
     1,  0,  0, -1,
    -1,  0,  0,  1,
     0,  1, -1,  0
  };

  uint8_t currentState = readEncoderState();

  if (currentState == lastEncoderState) {
    return;
  }

  if (displaySleeping) {
    lastEncoderState = currentState;
    encoderAccumulator = 0;
    wakeController("ENCODER");
    return;
  }

  markLocalActivity();

  unsigned long nowUs = micros();

  if (nowUs - lastEncoderTransitionUs < encoderTransitionDebounceUs) {
    return;
  }

  uint8_t transitionIndex = (lastEncoderState << 2) | currentState;
  int8_t movement = transitionTable[transitionIndex];

  lastEncoderState = currentState;
  lastEncoderTransitionUs = nowUs;

  if (movement == 0) {
    // Ignore invalid quadrature transitions caused by contact bounce.
    encoderAccumulator = 0;
    return;
  }

  if ((encoderAccumulator > 0 && movement < 0) || (encoderAccumulator < 0 && movement > 0)) {
    // A sudden single reverse transition during a turn is usually bounce.
    // Reset to the new direction instead of immediately reporting reverse movement.
    encoderAccumulator = movement;
  } else {
    encoderAccumulator += movement;
  }

  unsigned long nowMs = millis();

  if (encoderAccumulator >= encoderCountsPerDetent && nowMs - lastEncoderReportMs >= encoderReportGuardMs) {
    sendEncoderDelta(1);
    encoderAccumulator = 0;
    lastEncoderReportMs = nowMs;
  } else if (encoderAccumulator <= -encoderCountsPerDetent && nowMs - lastEncoderReportMs >= encoderReportGuardMs) {
    sendEncoderDelta(-1);
    encoderAccumulator = 0;
    lastEncoderReportMs = nowMs;
  }
}
void readButton() {
  bool currentButtonState = digitalRead(PIN_ENC_SW);
  unsigned long now = millis();

  if (currentButtonState != lastButtonState && now - lastButtonChangeMs > buttonDebounceMs) {
    lastButtonChangeMs = now;
    lastButtonState = currentButtonState;
    markLocalActivity();

    if (displaySleeping && currentButtonState == LOW) {
      buttonPressed = false;
      wakeController("BUTTON");
      return;
    }

    if (currentButtonState == LOW) {
      buttonPressed = true;
      buttonDownMs = now;
    } else {
      if (buttonPressed) {
        unsigned long pressDuration = now - buttonDownMs;

        if (pressDuration >= longPressMs) {
          Serial.println("BTN_LONG,0");
        } else {
          Serial.println("BTN_SHORT,0");
        }

        buttonPressed = false;
      }
    }
  }
}

void checkConnectedDisplayIdle() {
  if (!displayOk || displaySleeping || !pcConnected) {
    return;
  }

  unsigned long now = millis();
  if (!connectedIdleActive && now - lastDisplayActivityMs >= connectedIdleTimeoutMs) {
    connectedIdleActive = true;
    if (connectedIdleAction.startsWith("DIM_")) {
      int idleDimPercent = constrain(connectedIdleAction.substring(4).toInt(), 10, 70);
      applyOledBrightnessPercent(min(oledBrightnessPercent, idleDimPercent));
      updateDisplay();
    } else {
      setDisplayPower(false);
    }
    Serial.print("OLED_IDLE_START,");
    Serial.println(connectedIdleAction);
  }
}

void checkPcTimeout() {
  unsigned long now = millis();

  if (pcConnected && now - lastPcMessageMs > pcTimeoutMs) {
    showDisconnected();
  }

  if (!pcConnected && !displaySleeping && now - lastLocalActivityMs > noDashboardSleepMs) {
    enterControllerSleep("DASHBOARD_DISCONNECTED");
  }
}

void setupDefaultChannelStates() {
  for (int i = 0; i < LOGICAL_CHANNEL_COUNT; i++) {
    channels[i].label = "Unassigned";
    channels[i].volume = 0;
    channels[i].muted = true;
    channels[i].status = "Waiting";
  }

  channels[0].label = "Master";
  channels[0].status = "Waiting";
}

void setup() {
  pinMode(PIN_ENC_CLK, INPUT_PULLUP);
  pinMode(PIN_ENC_DT, INPUT_PULLUP);
  pinMode(PIN_ENC_SW, INPUT_PULLUP);

  Serial.begin(115200);
  delay(300);

  Wire.begin(PIN_I2C_SDA, PIN_I2C_SCL);

  displayOk = display.begin(SSD1306_SWITCHCAPVCC, OLED_I2C_ADDRESS);

  if (!displayOk) {
    sendDebug("OLED init failed; continuing without display");
  }

  setupDefaultChannelStates();

  if (displayOk) {
    applyOledBrightness();
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(1);
    drawCenteredSmall("PC Volume", 8);
    drawCenteredSmall("Controller", 22);
    drawCenteredSmall("Waiting for PC", 44);
    display.display();
  }

  lastEncoderState = readEncoderState();
  encoderAccumulator = 0;
  lastEncoderTransitionUs = micros();
  lastLocalActivityMs = millis();
  lastDisplayActivityMs = millis();

  sendHello();
}

void loop() {
  readSerialMessages();
  readEncoder();
  readButton();
  checkConnectedDisplayIdle();
  checkPcTimeout();
}
