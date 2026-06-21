using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcVolumeControllerDashboard;

/// <summary>
/// WPF-specific bridge for the platform-agnostic <see cref="OledRenderer"/>.
/// Lives in the Windows host so Core stays free of any UI-framework dependency.
/// </summary>
internal static class OledRendererExtensions
{
    /// <summary>
    /// Builds a 128×64 WriteableBitmap (Bgr32) from the renderer's pixel buffer.
    /// White pixels = 0x00FFFFFF, black pixels = 0x00000000.
    /// </summary>
    public static WriteableBitmap ToWriteableBitmap(this OledRenderer renderer)
    {
        const int w = OledRenderer.Width;
        const int h = OledRenderer.Height;

        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        int stride = w * 4; // 4 bytes per pixel for Bgr32
        byte[] buffer = new byte[stride * h];

        ReadOnlySpan<bool> pixels = renderer.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int bufIdx = (y * w + x) * 4;
                if (pixels[y * w + x])
                {
                    buffer[bufIdx + 0] = 0xFF; // B
                    buffer[bufIdx + 1] = 0xFF; // G
                    buffer[bufIdx + 2] = 0xFF; // R
                    buffer[bufIdx + 3] = 0x00; // unused (Bgr32)
                }
                // else already 0x00000000 from Array initialisation
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, w, h), buffer, stride, 0);
        return bitmap;
    }
}
