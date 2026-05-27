using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcVolumeControllerDashboard
{
    /// <summary>
    /// Pixel-accurate replica of the Adafruit SSD1306 / GFX drawing API used by the firmware.
    /// Renders to a 128x64 monochrome pixel buffer and exports as a WriteableBitmap.
    /// </summary>
    public sealed class OledRenderer
    {
        private const int W = 128;
        private const int H = 64;

        private readonly bool[] _pixels = new bool[W * H];

        // ── Adafruit GFX glcdfont.c  (5 bytes per glyph, 96 printable ASCII chars 0x20–0x7E)
        // Column-major, bit 0 = top row of each column byte.
        private static readonly byte[] Font = new byte[]
        {
            // 0x20 space
            0x00,0x00,0x00,0x00,0x00,
            // 0x21 !
            0x00,0x00,0x5F,0x00,0x00,
            // 0x22 "
            0x00,0x07,0x00,0x07,0x00,
            // 0x23 #
            0x14,0x7F,0x14,0x7F,0x14,
            // 0x24 $
            0x24,0x2A,0x7F,0x2A,0x12,
            // 0x25 %
            0x23,0x13,0x08,0x64,0x62,
            // 0x26 &
            0x36,0x49,0x55,0x22,0x50,
            // 0x27 '
            0x00,0x05,0x03,0x00,0x00,
            // 0x28 (
            0x00,0x1C,0x22,0x41,0x00,
            // 0x29 )
            0x00,0x41,0x22,0x1C,0x00,
            // 0x2A *
            0x08,0x2A,0x1C,0x2A,0x08,
            // 0x2B +
            0x08,0x08,0x3E,0x08,0x08,
            // 0x2C ,
            0x00,0x50,0x30,0x00,0x00,
            // 0x2D -
            0x08,0x08,0x08,0x08,0x08,
            // 0x2E .
            0x00,0x60,0x60,0x00,0x00,
            // 0x2F /
            0x20,0x10,0x08,0x04,0x02,
            // 0x30 0
            0x3E,0x51,0x49,0x45,0x3E,
            // 0x31 1
            0x00,0x42,0x7F,0x40,0x00,
            // 0x32 2
            0x42,0x61,0x51,0x49,0x46,
            // 0x33 3
            0x21,0x41,0x45,0x4B,0x31,
            // 0x34 4
            0x18,0x14,0x12,0x7F,0x10,
            // 0x35 5
            0x27,0x45,0x45,0x45,0x39,
            // 0x36 6
            0x3C,0x4A,0x49,0x49,0x30,
            // 0x37 7
            0x01,0x71,0x09,0x05,0x03,
            // 0x38 8
            0x36,0x49,0x49,0x49,0x36,
            // 0x39 9
            0x06,0x49,0x49,0x29,0x1E,
            // 0x3A :
            0x00,0x36,0x36,0x00,0x00,
            // 0x3B ;
            0x00,0x56,0x36,0x00,0x00,
            // 0x3C <
            0x00,0x08,0x14,0x22,0x41,
            // 0x3D =
            0x14,0x14,0x14,0x14,0x14,
            // 0x3E >
            0x41,0x22,0x14,0x08,0x00,
            // 0x3F ?
            0x02,0x01,0x51,0x09,0x06,
            // 0x40 @
            0x32,0x49,0x79,0x41,0x3E,
            // 0x41 A
            0x7E,0x11,0x11,0x11,0x7E,
            // 0x42 B
            0x7F,0x49,0x49,0x49,0x36,
            // 0x43 C
            0x3E,0x41,0x41,0x41,0x22,
            // 0x44 D
            0x7F,0x41,0x41,0x22,0x1C,
            // 0x45 E
            0x7F,0x49,0x49,0x49,0x41,
            // 0x46 F
            0x7F,0x09,0x09,0x01,0x01,
            // 0x47 G
            0x3E,0x41,0x41,0x51,0x32,
            // 0x48 H
            0x7F,0x08,0x08,0x08,0x7F,
            // 0x49 I
            0x00,0x41,0x7F,0x41,0x00,
            // 0x4A J
            0x20,0x40,0x41,0x3F,0x01,
            // 0x4B K
            0x7F,0x08,0x14,0x22,0x41,
            // 0x4C L
            0x7F,0x40,0x40,0x40,0x40,
            // 0x4D M
            0x7F,0x02,0x04,0x02,0x7F,
            // 0x4E N
            0x7F,0x04,0x08,0x10,0x7F,
            // 0x4F O
            0x3E,0x41,0x41,0x41,0x3E,
            // 0x50 P
            0x7F,0x09,0x09,0x09,0x06,
            // 0x51 Q
            0x3E,0x41,0x51,0x21,0x5E,
            // 0x52 R
            0x7F,0x09,0x19,0x29,0x46,
            // 0x53 S
            0x46,0x49,0x49,0x49,0x31,
            // 0x54 T
            0x01,0x01,0x7F,0x01,0x01,
            // 0x55 U
            0x3F,0x40,0x40,0x40,0x3F,
            // 0x56 V
            0x1F,0x20,0x40,0x20,0x1F,
            // 0x57 W
            0x7F,0x20,0x18,0x20,0x7F,
            // 0x58 X
            0x63,0x14,0x08,0x14,0x63,
            // 0x59 Y
            0x03,0x04,0x78,0x04,0x03,
            // 0x5A Z
            0x61,0x51,0x49,0x45,0x43,
            // 0x5B [
            0x00,0x00,0x7F,0x41,0x41,
            // 0x5C backslash
            0x02,0x04,0x08,0x10,0x20,
            // 0x5D ]
            0x41,0x41,0x7F,0x00,0x00,
            // 0x5E ^
            0x04,0x02,0x01,0x02,0x04,
            // 0x5F _
            0x40,0x40,0x40,0x40,0x40,
            // 0x60 `
            0x00,0x01,0x02,0x04,0x00,
            // 0x61 a
            0x20,0x54,0x54,0x54,0x78,
            // 0x62 b
            0x7F,0x48,0x44,0x44,0x38,
            // 0x63 c
            0x38,0x44,0x44,0x44,0x20,
            // 0x64 d
            0x38,0x44,0x44,0x48,0x7F,
            // 0x65 e
            0x38,0x54,0x54,0x54,0x18,
            // 0x66 f
            0x08,0x7E,0x09,0x01,0x02,
            // 0x67 g
            0x08,0x14,0x54,0x54,0x3C,
            // 0x68 h
            0x7F,0x08,0x04,0x04,0x78,
            // 0x69 i
            0x00,0x44,0x7D,0x40,0x00,
            // 0x6A j
            0x20,0x40,0x44,0x3D,0x00,
            // 0x6B k
            0x00,0x7F,0x10,0x28,0x44,
            // 0x6C l
            0x00,0x41,0x7F,0x40,0x00,
            // 0x6D m
            0x7C,0x04,0x18,0x04,0x78,
            // 0x6E n
            0x7C,0x08,0x04,0x04,0x78,
            // 0x6F o
            0x38,0x44,0x44,0x44,0x38,
            // 0x70 p
            0x7C,0x14,0x14,0x14,0x08,
            // 0x71 q
            0x08,0x14,0x14,0x18,0x7C,
            // 0x72 r
            0x7C,0x08,0x04,0x04,0x08,
            // 0x73 s
            0x48,0x54,0x54,0x54,0x20,
            // 0x74 t
            0x04,0x3F,0x44,0x40,0x20,
            // 0x75 u
            0x3C,0x40,0x40,0x20,0x7C,
            // 0x76 v
            0x1C,0x20,0x40,0x20,0x1C,
            // 0x77 w
            0x3C,0x40,0x30,0x40,0x3C,
            // 0x78 x
            0x44,0x28,0x10,0x28,0x44,
            // 0x79 y
            0x0C,0x50,0x50,0x50,0x3C,
            // 0x7A z
            0x44,0x64,0x54,0x4C,0x44,
            // 0x7B {
            0x00,0x08,0x36,0x41,0x00,
            // 0x7C |
            0x00,0x00,0x7F,0x00,0x00,
            // 0x7D }
            0x00,0x41,0x36,0x08,0x00,
            // 0x7E ~
            0x08,0x08,0x2A,0x1C,0x08,
        };

        // ── Low-level pixel helpers ──────────────────────────────────────────────

        public void ClearDisplay()
        {
            Array.Clear(_pixels, 0, _pixels.Length);
        }

        private void SetPixel(int x, int y)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return;
            _pixels[y * W + x] = true;
        }

        /// <summary>Horizontal line only (firmware only draws h-lines).</summary>
        public void DrawHLine(int x0, int y, int len)
        {
            for (int i = 0; i < len; i++)
                SetPixel(x0 + i, y);
        }

        /// <summary>Outline rectangle (firmware uses drawRect).</summary>
        public void DrawRect(int x, int y, int w, int h)
        {
            // top & bottom
            for (int i = 0; i < w; i++)
            {
                SetPixel(x + i, y);
                SetPixel(x + i, y + h - 1);
            }
            // left & right
            for (int j = 0; j < h; j++)
            {
                SetPixel(x, y + j);
                SetPixel(x + w - 1, y + j);
            }
        }

        /// <summary>Filled rectangle (firmware uses fillRect).</summary>
        public void FillRect(int x, int y, int w, int h)
        {
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    SetPixel(x + i, y + j);
        }

        // ── Text rendering ───────────────────────────────────────────────────────

        /// <summary>Renders one character using the Adafruit GFX 5×7 bitmap font.</summary>
        private void DrawChar(int x, int y, char c, int size)
        {
            if (c < 0x20 || c > 0x7E) c = ' ';
            int idx = (c - 0x20) * 5;

            for (int col = 0; col < 5; col++)
            {
                byte colData = Font[idx + col];
                for (int row = 0; row < 7; row++) // 7 active rows (row 7 is gap)
                {
                    if ((colData & (1 << row)) != 0)
                    {
                        // Scale the pixel by `size`
                        int px = x + col * size;
                        int py = y + row * size;
                        for (int sy = 0; sy < size; sy++)
                            for (int sx = 0; sx < size; sx++)
                                SetPixel(px + sx, py + sy);
                    }
                }
            }
        }

        /// <summary>Renders a string left to right; each char occupies 6*size pixels wide.</summary>
        private void DrawString(string text, int x, int y, int size)
        {
            if (text == null) return;
            int cursorX = x;
            foreach (char c in text)
            {
                DrawChar(cursorX, y, c, size);
                cursorX += 6 * size;
            }
        }

        private int TextWidth(string text, int size) => (text?.Length ?? 0) * 6 * size;

        /// <summary>Centers text horizontally then calls DrawString.</summary>
        private void DrawCenteredSmall(string text, int y, int size)
        {
            if (text == null) text = string.Empty;
            int tw = TextWidth(text, size);
            int x = Math.Max(0, (W - tw) / 2);
            DrawString(text, x, y, size);
        }

        // ── Firmware display modes ────────────────────────────────────────────────

        /// <summary>Mode: App name + volume (default/else branch in firmware).</summary>
        public void RenderAppVolume(string label, int volume, bool muted, string status)
        {
            ClearDisplay();
            DrawCenteredSmall(label, 0, 1);
            DrawHLine(0, 12, 128);
            string volumeText = $"{volume}%";
            DrawString(volumeText, (128 - TextWidth(volumeText, 2)) / 2, 22, 2);
            DrawCenteredSmall(muted ? "Muted" : "Unmuted", 46, 1);
            DrawCenteredSmall(status, 56, 1);
        }

        /// <summary>Mode: Large volume number.</summary>
        public void RenderLargeVolume(string label, int volume, bool muted)
        {
            ClearDisplay();
            DrawCenteredSmall(label, 0, 1);
            DrawHLine(0, 12, 128);
            string volumeText = $"{volume}%";
            DrawString(volumeText, (128 - TextWidth(volumeText, 3)) / 2, 24, 3);
            DrawCenteredSmall(muted ? "Muted" : "Unmuted", 56, 1);
        }

        /// <summary>Mode: Mute status.</summary>
        public void RenderMuteStatus(string label, int volume, bool muted)
        {
            ClearDisplay();
            DrawCenteredSmall(muted ? "MUTED" : "ACTIVE", 12, 2);
            DrawCenteredSmall(label, 40, 1);
            DrawCenteredSmall($"{volume}%", 54, 1);
        }

        /// <summary>Mode: App/device name (channel scroll).</summary>
        public void RenderAppOrDeviceName(int channelNum, string label, string status, int volume)
        {
            ClearDisplay();
            DrawCenteredSmall($"CHANNEL {channelNum}", 0, 1);
            DrawHLine(0, 12, 128);
            DrawCenteredSmall(label, 24, 1);
            DrawCenteredSmall(status, 40, 1);
            DrawCenteredSmall($"{volume}%", 54, 1);
        }

        /// <summary>Mode: Bar/percentage view.</summary>
        public void RenderBarPercent(string label, int volume, bool muted)
        {
            ClearDisplay();
            DrawCenteredSmall(label, 0, 1);
            DrawHLine(0, 12, 128);
            DrawRect(8, 28, 112, 14);
            int barWidth = (int)Math.Round(Math.Clamp(volume, 0, 100) / 100.0 * 108);
            if (barWidth > 0) FillRect(10, 30, barWidth, 10);
            DrawCenteredSmall($"{volume}%", 48, 1);
            DrawCenteredSmall(muted ? "Muted" : "Unmuted", 56, 1);
        }

        // ── Bitmap export ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a 128×64 WriteableBitmap (Bgr32).
        /// White pixels = 0x00FFFFFF, black pixels = 0x00000000.
        /// </summary>
        public WriteableBitmap ToWriteableBitmap()
        {
            var bitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgr32, null);
            int stride = W * 4; // 4 bytes per pixel for Bgr32
            byte[] buffer = new byte[stride * H];

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int bufIdx = (y * W + x) * 4;
                    if (_pixels[y * W + x])
                    {
                        buffer[bufIdx + 0] = 0xFF; // B
                        buffer[bufIdx + 1] = 0xFF; // G
                        buffer[bufIdx + 2] = 0xFF; // R
                        buffer[bufIdx + 3] = 0x00; // unused (Bgr32)
                    }
                    // else already 0x00000000 from Array initialisation
                }
            }

            bitmap.WritePixels(new Int32Rect(0, 0, W, H), buffer, stride, 0);
            return bitmap;
        }
    }
}
