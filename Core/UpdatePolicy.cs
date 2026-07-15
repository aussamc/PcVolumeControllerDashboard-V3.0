using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure decision helpers for the v3.19 auto-update engine, kept host-free so they
/// unit-test without a network or timer. The host layer owns the actual GitHub query
/// (<c>UpdateCheckService</c>) and the timers; this class only answers "should a check
/// run now?" and "should the user be prompted about this release?".
/// </summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Whether a launch/interval auto-check should run now. Returns <c>false</c> when the
    /// checker is disabled (<paramref name="autoCheckEnabled"/> false) or the app is in
    /// <paramref name="safeMode"/>; otherwise <c>true</c> when at least
    /// <paramref name="minInterval"/> has elapsed since <paramref name="lastCheckUtc"/>.
    /// A default (never-checked) <paramref name="lastCheckUtc"/> always checks, and a
    /// <see cref="TimeSpan.Zero"/> interval means "no throttle" (periodic ticks).
    /// </summary>
    public static bool ShouldAutoCheck(bool autoCheckEnabled, bool safeMode,
        DateTime lastCheckUtc, DateTime nowUtc, TimeSpan minInterval)
    {
        if (!autoCheckEnabled || safeMode)
            return false;
        if (lastCheckUtc == default)
            return true;
        return nowUtc - lastCheckUtc >= minInterval;
    }

    /// <summary>
    /// Whether to surface an "update available" prompt for <paramref name="latestVersion"/>.
    /// Suppressed when no update was found (<paramref name="updateAvailable"/> false) or
    /// when the user has skipped exactly this version. A strictly newer release later
    /// clears the skip implicitly because its version no longer matches the skipped one.
    /// </summary>
    public static bool ShouldPrompt(bool updateAvailable, string? latestVersion, string? skippedVersion)
    {
        if (!updateAvailable)
            return false;
        if (string.IsNullOrWhiteSpace(skippedVersion))
            return true;
        return !string.Equals(latestVersion?.Trim(), skippedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
