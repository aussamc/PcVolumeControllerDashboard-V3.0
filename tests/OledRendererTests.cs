using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Tests for the Large Volume Number layout (v3.16 item 9b). The renderer is a
/// pixel replica of the firmware LARGE_VOLUME branch: channel name at size 2, a
/// rule at y18, then a size-4 value that becomes the word "MUTE" when muted —
/// with no separate bottom mute strip.
/// </summary>
public sealed class OledRendererTests
{
    private const int W = OledRenderer.Width;   // 128
    private const int H = OledRenderer.Height;  // 64

    /// <summary>Counts lit pixels within the inclusive-exclusive vertical band [yStart, yEnd).</summary>
    private static int LitInBand(OledRenderer r, int yStart, int yEnd)
    {
        bool[] px = r.Pixels.ToArray();
        int count = 0;
        for (int y = yStart; y < yEnd; y++)
            for (int x = 0; x < W; x++)
                if (px[y * W + x]) count++;
        return count;
    }

    [Fact]
    public void RenderLargeVolume_Unmuted_DrawsHeaderAndBigNumber()
    {
        var r = new OledRenderer();
        r.RenderLargeVolume("Master", 50, muted: false);

        // Size-2 header near the top.
        LitInBand(r, 0, 16).Should().BeGreaterThan(0, "the channel name renders as a size-2 header");
        // Size-4 number occupies the large middle band.
        LitInBand(r, 26, 58).Should().BeGreaterThan(0, "the volume number renders large in the middle");
        // No content in the old bottom mute-strip rows.
        LitInBand(r, 58, H).Should().Be(0, "the separate mute strip has been removed");
    }

    [Fact]
    public void RenderLargeVolume_Muted_ReplacesNumberWithMuteInBigBand()
    {
        var muted = new OledRenderer();
        muted.RenderLargeVolume("Master", 50, muted: true);

        // "MUTE" occupies the same large middle band as the number would.
        LitInBand(muted, 26, 58).Should().BeGreaterThan(0, "\"MUTE\" fills the big value region when muted");
        LitInBand(muted, 58, H).Should().Be(0, "no bottom mute strip in either state");
    }

    [Fact]
    public void RenderLargeVolume_MutedAndUnmuted_ProduceDifferentBuffers()
    {
        var unmuted = new OledRenderer();
        unmuted.RenderLargeVolume("Master", 50, muted: false);
        var muted = new OledRenderer();
        muted.RenderLargeVolume("Master", 50, muted: true);

        unmuted.Pixels.ToArray().Should().NotEqual(muted.Pixels.ToArray(),
            "muted shows \"MUTE\" while unmuted shows the volume number");
    }

    [Fact]
    public void RenderLargeVolume_BigTextStaysWithinPanelWidth()
    {
        // Widest realistic value is "100%" (4 chars) at size 4 = 4*6*4 = 96px <= 128,
        // so it never clips horizontally.
        var r = new OledRenderer();
        r.RenderLargeVolume("Master", 100, muted: false);

        bool[] px = r.Pixels.ToArray();
        // In the big-number band the edge columns must stay dark (centred, not
        // overflowing). The y18 rule spans full width by design, so it's excluded.
        for (int y = 26; y < 58; y++)
        {
            px[y * W + 0].Should().BeFalse();
            px[y * W + (W - 1)].Should().BeFalse();
        }
    }
}
