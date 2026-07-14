using System;
using System.Collections.Generic;
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

    public static IEnumerable<object[]> AllModes()
    {
        yield return new object[] { "AppVolume",      (Action<OledRenderer>)(r => r.RenderAppVolume("Browser", 73, false, "Active")) };
        yield return new object[] { "LargeVolume",    (Action<OledRenderer>)(r => r.RenderLargeVolume("Master", 100, false)) };
        yield return new object[] { "LargeVolumeMute",(Action<OledRenderer>)(r => r.RenderLargeVolume("Master", 100, true)) };
        yield return new object[] { "MuteStatus",     (Action<OledRenderer>)(r => r.RenderMuteStatus("Music", 40, true)) };
        yield return new object[] { "AppOrDeviceName",(Action<OledRenderer>)(r => r.RenderAppOrDeviceName(3, "Speakers", "Active", 88)) };
        yield return new object[] { "BarPercent",     (Action<OledRenderer>)(r => r.RenderBarPercent("Game", 55, false)) };
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void AllModes_ReserveBottomAntiBurnMargin(string name, Action<OledRenderer> render)
    {
        // Every mode must keep content within rows 0..(63 - AntiBurnMaxOffset) so the
        // firmware's 0..3px SETDISPLAYOFFSET shift never wraps a lit pixel (item 11).
        var r = new OledRenderer();
        render(r);

        int firstReservedRow = OledRenderer.Height - OledRenderer.AntiBurnMaxOffset; // 61
        LitInBand(r, firstReservedRow, OledRenderer.Height).Should().Be(0,
            $"{name} must leave the bottom {OledRenderer.AntiBurnMaxOffset} rows clear for the anti-burn shift");
    }

    [Fact]
    public void ApplyDisplayOffset_ShiftsContentDownWithWrap()
    {
        var r = new OledRenderer();
        r.RenderLargeVolume("Master", 100, muted: false);
        // The rule is a full-width lit row at y18; nothing is lit at y21 yet.
        LitInBand(r, 18, 19).Should().Be(W);
        LitInBand(r, 21, 22).Should().Be(0);

        r.ApplyDisplayOffset(3);

        // After a 3px downward shift the rule moves to y21; y18 is now clear.
        LitInBand(r, 21, 22).Should().Be(W, "the rule shifts down by the offset");
        LitInBand(r, 18, 19).Should().Be(0);
    }

    [Fact]
    public void ApplyDisplayOffset_Zero_IsNoOp()
    {
        var a = new OledRenderer();
        a.RenderLargeVolume("Master", 50, muted: false);
        var b = new OledRenderer();
        b.RenderLargeVolume("Master", 50, muted: false);

        a.ApplyDisplayOffset(0);

        a.Pixels.ToArray().Should().Equal(b.Pixels.ToArray());
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
