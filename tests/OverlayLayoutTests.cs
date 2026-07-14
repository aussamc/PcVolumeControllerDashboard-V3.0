using FluentAssertions;
using PcVolumeControllerDashboard.Core;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure overlay geometry/clamp helpers (v3.15 overlay enhancements).
/// </summary>
public sealed class OverlayLayoutTests
{
    // ── Clamp helpers ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, OverlayLayout.MinScale)]   // below floor
    [InlineData(2.0, OverlayLayout.MaxScale)]   // above ceiling
    [InlineData(1.25, 1.25)]                     // in range, untouched
    public void ClampScale_BoundsToRange(double input, double expected)
    {
        OverlayLayout.ClampScale(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.0, OverlayLayout.MinOpacity)] // below floor keeps it visible
    [InlineData(1.5, OverlayLayout.MaxOpacity)] // above ceiling
    [InlineData(0.6, 0.6)]
    public void ClampOpacity_BoundsToRange(double input, double expected)
    {
        OverlayLayout.ClampOpacity(input).Should().Be(expected);
    }

    // ── Position ──────────────────────────────────────────────────────────────────
    // A 2000×1000 working area at origin, a 320×84 window, 28px margin.

    private const int AreaX = 0, AreaY = 0, AreaW = 2000, AreaH = 1000;
    private const int WinW = 320, WinH = 84, Margin = 28;

    private static (int X, int Y) Pos(string position) =>
        OverlayLayout.Position(AreaX, AreaY, AreaW, AreaH, WinW, WinH, Margin, position);

    [Fact]
    public void Position_Corners_RespectMarginAndSize()
    {
        Pos("TopLeft").Should().Be((28, 28));
        Pos("TopRight").Should().Be((2000 - 320 - 28, 28));
        Pos("BottomLeft").Should().Be((28, 1000 - 84 - 28));
        Pos("BottomRight").Should().Be((2000 - 320 - 28, 1000 - 84 - 28));
    }

    [Fact]
    public void Position_Centers_HorizontallyCentered()
    {
        int centerX = (2000 - 320) / 2;
        Pos("TopCenter").Should().Be((centerX, 28));
        Pos("BottomCenter").Should().Be((centerX, 1000 - 84 - 28));
    }

    [Fact]
    public void Position_NullOrUnknown_FallsBackToBottomCenter()
    {
        var expected = Pos("BottomCenter");
        Pos(null!).Should().Be(expected);
        Pos("Nonsense").Should().Be(expected);
    }

    [Fact]
    public void Position_HonorsNonZeroAreaOrigin()
    {
        // Secondary monitor to the right: origin (2000, 0).
        var (x, y) = OverlayLayout.Position(2000, 0, 1920, 1080, WinW, WinH, Margin, "TopLeft");
        x.Should().Be(2000 + 28);
        y.Should().Be(28);
    }
}
