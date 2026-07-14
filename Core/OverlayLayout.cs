using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure geometry and setting-clamp helpers for the on-screen volume overlay, extracted
/// from the Avalonia <c>VolumeOverlay</c> window so the position math and the
/// opacity/scale bounds are unit-testable without a windowing stack.
/// </summary>
public static class OverlayLayout
{
    // Appearance bounds (v3.15). Scale 1.0 = default size; opacity 1.0 = fully opaque.
    // The opacity floor keeps the popup from becoming effectively invisible.
    public const double MinScale = 0.75;
    public const double MaxScale = 1.50;
    public const double MinOpacity = 0.30;
    public const double MaxOpacity = 1.0;

    public static double ClampScale(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    public static double ClampOpacity(double opacity) => Math.Clamp(opacity, MinOpacity, MaxOpacity);

    /// <summary>
    /// Computes the top-left position of the overlay within a screen's working area.
    /// All inputs and the result are in the same coordinate space (physical pixels at
    /// the call site). <paramref name="position"/> is matched case-insensitively:
    /// "Left"/"Right" (else horizontally centered) and a "Top" prefix (else bottom),
    /// e.g. "TopLeft", "BottomCenter". A null/unknown value falls back to bottom-center.
    /// </summary>
    public static (int X, int Y) Position(
        int areaX, int areaY, int areaWidth, int areaHeight,
        int windowWidth, int windowHeight, int margin, string? position)
    {
        string pos = position ?? "BottomCenter";
        int right = areaX + areaWidth;
        int bottom = areaY + areaHeight;

        int x = pos.Contains("Left", StringComparison.OrdinalIgnoreCase) ? areaX + margin
              : pos.Contains("Right", StringComparison.OrdinalIgnoreCase) ? right - windowWidth - margin
              : areaX + (areaWidth - windowWidth) / 2;
        int y = pos.StartsWith("Top", StringComparison.OrdinalIgnoreCase) ? areaY + margin
              : bottom - windowHeight - margin;

        return (x, y);
    }
}
