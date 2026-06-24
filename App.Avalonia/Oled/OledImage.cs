using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Oled;

/// <summary>
/// Avalonia bridge for the platform-agnostic Core <see cref="OledRenderer"/>.
/// Builds a 128×64 <see cref="WriteableBitmap"/> from the renderer's monochrome
/// pixel buffer — the cross-platform counterpart to the WPF host's
/// <c>OledRenderer.ToWriteableBitmap</c> extension (which is WPF-only). Lives in
/// the Avalonia host so Core stays free of any UI-framework dependency.
/// </summary>
public static class OledImage
{
    /// <summary>
    /// Renders the OLED buffer to a Bgra8888 <see cref="WriteableBitmap"/>:
    /// lit pixels → opaque white, unlit → opaque black (the panel's appearance).
    /// </summary>
    public static WriteableBitmap Build(OledRenderer renderer)
    {
        const int w = OledRenderer.Width;
        const int h = OledRenderer.Height;

        var bitmap = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        ReadOnlySpan<bool> pixels = renderer.Pixels;

        // Pack one row at a time so any framebuffer row padding (RowBytes) is honoured.
        byte[] row = new byte[w * 4];
        using ILockedFramebuffer fb = bitmap.Lock();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = pixels[y * w + x] ? (byte)0xFF : (byte)0x00;
                int b = x * 4;
                row[b + 0] = v;     // B
                row[b + 1] = v;     // G
                row[b + 2] = v;     // R
                row[b + 3] = 0xFF;  // A (opaque)
            }
            Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
        }

        return bitmap;
    }
}
