using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure encoder-feel math extracted into Core.EncoderMath.
/// </summary>
public sealed class EncoderMathTests
{
    private const int MaxStep = 25; // matches MainWindow.MaxVolumeStepPercent

    // ── GetAcceleratedStep: fixed presets ─────────────────────────────────────────

    [Fact]
    public void AcceleratedStep_NonePreset_ReturnsBaseStep()
    {
        int step = EncoderMath.GetAcceleratedStep(2, intervalMs: 5, AccelerationPresets.None, 150, 8f, 0.5f, MaxStep);
        step.Should().Be(2, "the None preset never scales the base step");
    }

    [Theory]
    [InlineData(90, 1)]   // >= 80 ms → 1x
    [InlineData(50, 2)]   // < 80 ms  → 2x
    public void AcceleratedStep_LightPreset_AppliesThreshold(double intervalMs, int expectedMultiple)
    {
        int step = EncoderMath.GetAcceleratedStep(1, intervalMs, AccelerationPresets.Light, 150, 8f, 0.5f, MaxStep);
        step.Should().Be(expectedMultiple);
    }

    [Theory]
    [InlineData(120, 1)]  // >= 100 ms → 1x
    [InlineData(80, 2)]   // < 100 ms  → 2x
    [InlineData(40, 3)]   // < 60 ms   → 3x
    public void AcceleratedStep_MediumPreset_AppliesThresholds(double intervalMs, int expectedMultiple)
    {
        int step = EncoderMath.GetAcceleratedStep(1, intervalMs, AccelerationPresets.Medium, 150, 8f, 0.5f, MaxStep);
        step.Should().Be(expectedMultiple);
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(100, 2)]  // < 110 ms
    [InlineData(60, 3)]   // < 70 ms
    [InlineData(40, 4)]   // < 50 ms
    public void AcceleratedStep_AggressivePreset_AppliesThresholds(double intervalMs, int expectedMultiple)
    {
        int step = EncoderMath.GetAcceleratedStep(1, intervalMs, AccelerationPresets.Aggressive, 150, 8f, 0.5f, MaxStep);
        step.Should().Be(expectedMultiple);
    }

    [Fact]
    public void AcceleratedStep_ClampsToMax()
    {
        // Aggressive 4x on a large base step must clamp to the max.
        int step = EncoderMath.GetAcceleratedStep(20, intervalMs: 10, AccelerationPresets.Aggressive, 150, 8f, 0.5f, MaxStep);
        step.Should().Be(MaxStep);
    }

    [Fact]
    public void AcceleratedStep_NeverBelowOne()
    {
        int step = EncoderMath.GetAcceleratedStep(0, intervalMs: 500, AccelerationPresets.None, 150, 8f, 0.5f, MaxStep);
        step.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── GetAcceleratedStep / ComputeCustomAccelMultiplier: custom preset ───────────

    [Fact]
    public void CustomMultiplier_AtOrAboveThreshold_IsOne()
    {
        float m = EncoderMath.ComputeCustomAccelMultiplier(intervalMs: 200, thresholdMs: 150, maxMult: 8f, curveExp: 0.5f);
        m.Should().BeApproximately(1.0f, 0.0001f);
    }

    [Fact]
    public void CustomMultiplier_AtZeroInterval_IsMax()
    {
        float m = EncoderMath.ComputeCustomAccelMultiplier(intervalMs: 0, thresholdMs: 150, maxMult: 8f, curveExp: 0.5f);
        m.Should().BeApproximately(8.0f, 0.0001f);
    }

    [Fact]
    public void CustomMultiplier_IsMonotonicAsTurnSpeedsUp()
    {
        float slow = EncoderMath.ComputeCustomAccelMultiplier(120, 150, 8f, 1.0f);
        float fast = EncoderMath.ComputeCustomAccelMultiplier(30, 150, 8f, 1.0f);
        fast.Should().BeGreaterThan(slow);
    }

    [Fact]
    public void AcceleratedStep_CustomPreset_UsesContinuousFormula()
    {
        // At zero interval with maxMult 8x, base 2 → 16, clamped to MaxStep (25 → 16 fits).
        int step = EncoderMath.GetAcceleratedStep(2, intervalMs: 0, AccelerationPresets.Custom, 150, 8f, 1.0f, MaxStep);
        step.Should().Be(16);
    }

    // ── Smoothing ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SmoothingSpeed.Fast, 0.50f)]
    [InlineData(SmoothingSpeed.Normal, 0.35f)]
    [InlineData(SmoothingSpeed.Slow, 0.22f)]
    [InlineData("nonsense", 0.35f)]   // unknown falls back to Normal
    public void SmoothingAlpha_MapsSpeedToFactor(string speed, float expected)
    {
        EncoderMath.GetSmoothingAlpha(speed).Should().BeApproximately(expected, 0.0001f);
    }

    [Fact]
    public void EmaStep_MovesTowardTargetByAlpha()
    {
        // halfway target, alpha 0.5 → quarter of the way after one step
        float next = EncoderMath.EmaStep(current: 0.0f, target: 1.0f, alpha: 0.5f);
        next.Should().BeApproximately(0.5f, 0.0001f);
    }

    [Fact]
    public void EmaStep_ConvergesToTargetOverManyTicks()
    {
        float v = 0f;
        for (int i = 0; i < 50; i++)
            v = EncoderMath.EmaStep(v, 1.0f, 0.35f);
        v.Should().BeApproximately(1.0f, 0.001f);
    }
}
