using System;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure <see cref="UpdatePolicy"/> decision helpers that drive the
/// v3.19 auto-update checker (should-check throttling and should-prompt de-duplication).
/// </summary>
public sealed class UpdatePolicyTests
{
    private static readonly DateTime Now = new(2026, 07, 15, 12, 00, 00, DateTimeKind.Utc);
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    // ── ShouldAutoCheck ───────────────────────────────────────────────────────────

    [Fact]
    public void ShouldAutoCheck_Disabled_IsFalse()
    {
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: false, safeMode: false,
            lastCheckUtc: default, nowUtc: Now, minInterval: FourHours).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoCheck_SafeMode_IsFalse()
    {
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: true, safeMode: true,
            lastCheckUtc: default, nowUtc: Now, minInterval: FourHours).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoCheck_NeverChecked_IsTrue()
    {
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: true, safeMode: false,
            lastCheckUtc: default, nowUtc: Now, minInterval: FourHours).Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoCheck_WithinThrottle_IsFalse()
    {
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: true, safeMode: false,
            lastCheckUtc: Now.AddHours(-1), nowUtc: Now, minInterval: FourHours).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoCheck_PastThrottle_IsTrue()
    {
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: true, safeMode: false,
            lastCheckUtc: Now.AddHours(-5), nowUtc: Now, minInterval: FourHours).Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoCheck_ZeroInterval_NoThrottle()
    {
        // Periodic ticks pass TimeSpan.Zero so any elapsed time (even a recent check) checks.
        UpdatePolicy.ShouldAutoCheck(autoCheckEnabled: true, safeMode: false,
            lastCheckUtc: Now.AddMinutes(-1), nowUtc: Now, minInterval: TimeSpan.Zero).Should().BeTrue();
    }

    // ── ShouldPrompt ──────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldPrompt_NoUpdate_IsFalse()
    {
        UpdatePolicy.ShouldPrompt(updateAvailable: false, latestVersion: "3.20", skippedVersion: "")
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldPrompt_UpdateAndNothingSkipped_IsTrue()
    {
        UpdatePolicy.ShouldPrompt(updateAvailable: true, latestVersion: "3.20", skippedVersion: "")
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldPrompt_SkippedSameVersion_IsFalse()
    {
        UpdatePolicy.ShouldPrompt(updateAvailable: true, latestVersion: "3.20", skippedVersion: "3.20")
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldPrompt_SkippedOlderVersion_NewerStillPrompts()
    {
        // User skipped 3.20; a later 3.21 no longer matches the skip, so prompt again.
        UpdatePolicy.ShouldPrompt(updateAvailable: true, latestVersion: "3.21", skippedVersion: "3.20")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(" 3.20 ", "3.20")]  // surrounding whitespace ignored
    [InlineData("3.20", " 3.20")]
    public void ShouldPrompt_SkipMatchTrimsWhitespace(string latest, string skipped)
    {
        UpdatePolicy.ShouldPrompt(updateAvailable: true, latestVersion: latest, skippedVersion: skipped)
            .Should().BeFalse();
    }
}
