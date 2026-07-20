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

    // 18 chars — the longest label MakeProtocolSafeLabel can deliver.
    private const string MaxLenLabel = "EighteenCharsLabel";

    public static IEnumerable<object[]> AllModes()
    {
        yield return new object[] { "AppVolume",      (Action<OledRenderer>)(r => r.RenderAppVolume("Browser", 73, false, "Active")) };
        yield return new object[] { "AppVolumeLong",  (Action<OledRenderer>)(r => r.RenderAppVolume(MaxLenLabel, 100, true, MaxLenLabel)) };
        yield return new object[] { "LargeVolume",    (Action<OledRenderer>)(r => r.RenderLargeVolume("Master", 100, false)) };
        yield return new object[] { "LargeVolumeMute",(Action<OledRenderer>)(r => r.RenderLargeVolume("Master", 100, true)) };
        yield return new object[] { "LargeVolumeLong",(Action<OledRenderer>)(r => r.RenderLargeVolume(MaxLenLabel, 100, false)) };
        yield return new object[] { "MuteStatus",     (Action<OledRenderer>)(r => r.RenderMuteStatus("Music", 40, true)) };
        yield return new object[] { "MuteStatusLong", (Action<OledRenderer>)(r => r.RenderMuteStatus(MaxLenLabel, 100, false)) };
        yield return new object[] { "AppOrDeviceName",(Action<OledRenderer>)(r => r.RenderAppOrDeviceName(3, "Speakers", "Active", 88)) };
        yield return new object[] { "AppOrDeviceLong",(Action<OledRenderer>)(r => r.RenderAppOrDeviceName(6, MaxLenLabel, MaxLenLabel, 100)) };
        yield return new object[] { "BarPercent",     (Action<OledRenderer>)(r => r.RenderBarPercent("Game", 55, false)) };
        yield return new object[] { "BarPercentLong", (Action<OledRenderer>)(r => r.RenderBarPercent(MaxLenLabel, 100, false)) };
    }

    /// <summary>Counts lit pixels within the inclusive-exclusive column band [xStart, xEnd).</summary>
    private static int LitInColumns(OledRenderer r, int xStart, int xEnd)
    {
        bool[] px = r.Pixels.ToArray();
        int count = 0;
        for (int y = 0; y < H; y++)
            for (int x = xStart; x < xEnd; x++)
                if (px[y * W + x]) count++;
        return count;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void AllModes_FitWithinJitterSafeRegion(string name, Action<OledRenderer> render)
    {
        // Every mode must keep its base content within x0..125 / y0..61 so the
        // firmware's 0..2px 2-D anti-burn jitter (v2.31) never clips a lit pixel.
        var r = new OledRenderer();
        render(r);

        int firstReservedRow = OledRenderer.Height - OledRenderer.AntiBurnJitterMax;  // 62
        int firstReservedCol = OledRenderer.Width - OledRenderer.AntiBurnJitterMax;   // 126
        LitInBand(r, firstReservedRow, OledRenderer.Height).Should().Be(0,
            $"{name} must leave the bottom {OledRenderer.AntiBurnJitterMax} rows clear for the anti-burn jitter");
        LitInColumns(r, firstReservedCol, OledRenderer.Width).Should().Be(0,
            $"{name} must leave the right {OledRenderer.AntiBurnJitterMax} columns clear for the anti-burn jitter");
    }

    [Fact]
    public void SetAntiBurnJitter_ShiftsContentWithoutWrapping()
    {
        var baseline = new OledRenderer();
        baseline.RenderLargeVolume("Master", 100, muted: false);

        var jittered = new OledRenderer();
        jittered.SetAntiBurnJitter(2, 2);
        jittered.RenderLargeVolume("Master", 100, muted: false);

        // Every lit pixel moves exactly (+2, +2); nothing wraps to the opposite edge.
        bool[] a = baseline.Pixels.ToArray();
        bool[] b = jittered.Pixels.ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool expected = x >= 2 && y >= 2 && a[(y - 2) * W + (x - 2)];
                b[y * W + x].Should().Be(expected, $"pixel ({x},{y}) must be the baseline pixel shifted by (2,2)");
            }
    }

    [Fact]
    public void SetAntiBurnJitter_Zero_IsNoOp()
    {
        var a = new OledRenderer();
        a.SetAntiBurnJitter(0, 0);
        a.RenderLargeVolume("Master", 50, muted: false);
        var b = new OledRenderer();
        b.RenderLargeVolume("Master", 50, muted: false);

        a.Pixels.ToArray().Should().Equal(b.Pixels.ToArray());
    }

    [Fact]
    public void AntiBurnJitterWalk_CoversAllNinePositionsInAdjacentSteps()
    {
        var seen = new HashSet<(int, int)>();
        (int px, int py) = OledRenderer.AntiBurnJitterForStep(OledRenderer.AntiBurnJitterSteps - 1);
        for (int step = 0; step < OledRenderer.AntiBurnJitterSteps; step++)
        {
            (int dx, int dy) = OledRenderer.AntiBurnJitterForStep(step);
            dx.Should().BeInRange(0, OledRenderer.AntiBurnJitterMax);
            dy.Should().BeInRange(0, OledRenderer.AntiBurnJitterMax);
            // Each step (including the wrap from the last back to the first) moves
            // at most one pixel per axis — a walk, not a jump.
            Math.Abs(dx - px).Should().BeLessThanOrEqualTo(1);
            Math.Abs(dy - py).Should().BeLessThanOrEqualTo(1);
            seen.Add((dx, dy));
            (px, py) = (dx, dy);
        }
        seen.Should().HaveCount(OledRenderer.AntiBurnJitterSteps, "the walk visits every position of the 3×3 grid");
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
        // overflowing). The y20 rule spans full width by design, but sits above this band.
        for (int y = 26; y < 58; y++)
        {
            px[y * W + 0].Should().BeFalse();
            px[y * W + (W - 1)].Should().BeFalse();
        }
    }
}
