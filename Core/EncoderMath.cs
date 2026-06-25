namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure, side-effect-free encoder feel math: acceleration step scaling and the
/// EMA volume-smoothing factor. Extracted from MainWindow.Encoder.cs so the
/// formulas can be unit-tested and shared across platform hosts. All inputs are
/// passed explicitly — this class holds no state and reads no settings.
/// </summary>
public static class EncoderMath
{
    /// <summary>
    /// Returns a volume step (percent) scaled up when the encoder is turned quickly.
    /// </summary>
    /// <param name="baseStep">The unscaled per-detent step (percent).</param>
    /// <param name="intervalMs">Milliseconds since the previous detent for this channel.</param>
    /// <param name="preset">One of <see cref="AccelerationPresets"/>.</param>
    /// <param name="thresholdMs">Custom-preset: interval at/above which no boost applies.</param>
    /// <param name="maxMultiplier">Custom-preset: step multiplier at maximum speed.</param>
    /// <param name="curveExponent">Custom-preset: ramp shape (&lt;1 early, 1 linear, &gt;1 late).</param>
    /// <param name="maxVolumeStepPercent">Upper clamp on the returned step.</param>
    public static int GetAcceleratedStep(
        int baseStep,
        double intervalMs,
        string preset,
        int thresholdMs,
        float maxMultiplier,
        float curveExponent,
        int maxVolumeStepPercent)
    {
        // Custom preset: continuous formula using three tunable parameters.
        if (preset == AccelerationPresets.Custom)
        {
            float multiplier = ComputeCustomAccelMultiplier(intervalMs, thresholdMs, maxMultiplier, curveExponent);
            return Math.Clamp((int)Math.Round(baseStep * multiplier), 1, maxVolumeStepPercent);
        }

        // Fixed presets: step-function multipliers for Light / Medium / Aggressive.
        int intMultiplier = preset switch
        {
            AccelerationPresets.Light      => intervalMs < 80  ? 2 : 1,
            AccelerationPresets.Medium     => intervalMs < 60  ? 3 : intervalMs < 100 ? 2 : 1,
            AccelerationPresets.Aggressive => intervalMs < 50  ? 4 : intervalMs < 70  ? 3 : intervalMs < 110 ? 2 : 1,
            _                              => 1,
        };
        return Math.Clamp(baseStep * intMultiplier, 1, maxVolumeStepPercent);
    }

    /// <summary>
    /// Custom-preset acceleration multiplier (1× slow → <paramref name="maxMult"/>× fast)
    /// at a given turn interval. Also used to render the acceleration-curve preview.
    /// </summary>
    public static float ComputeCustomAccelMultiplier(double intervalMs, int thresholdMs, float maxMult, float curveExp)
    {
        float threshold = Math.Max(1f, thresholdMs);
        float sf        = (float)Math.Clamp((threshold - intervalMs) / threshold, 0.0, 1.0);
        float curved    = MathF.Pow(sf, Math.Max(0.1f, curveExp));
        return 1.0f + (maxMult - 1.0f) * curved;
    }

    /// <summary>
    /// Translates an encoder sensitivity percentage into the unscaled per-detent
    /// volume step. 0 % → 1 (minimum); otherwise <c>baseStep × sensitivity/50</c>,
    /// rounded and clamped to [1, <paramref name="maxStep"/>]. Mirrors the WPF
    /// host's GetVolumeStepPercentFromSensitivity.
    /// </summary>
    public static int StepFromSensitivity(int sensitivityPercent, int baseStep, int maxStep, int maxSensitivity)
    {
        int s = Math.Clamp(sensitivityPercent, 0, maxSensitivity);
        if (s <= 0) return 1;
        int step = (int)Math.Round(baseStep * (s / 50.0));
        return Math.Clamp(step, 1, maxStep);
    }

    /// <summary>
    /// EMA smoothing factor (alpha) per ~16 ms tick for the given speed.
    /// After N ticks the remaining error is (1-alpha)^N.
    /// </summary>
    public static float GetSmoothingAlpha(string speed)
    {
        return speed switch
        {
            SmoothingSpeed.Fast => 0.50f,
            SmoothingSpeed.Slow => 0.22f,
            _                   => 0.35f,  // Normal
        };
    }

    /// <summary>
    /// One exponential-moving-average step toward <paramref name="target"/>:
    /// <c>current + alpha * (target - current)</c>.
    /// </summary>
    public static float EmaStep(float current, float target, float alpha)
        => current + alpha * (target - current);
}
