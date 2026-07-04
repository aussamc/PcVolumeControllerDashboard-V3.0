using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure version-comparison helpers for the software update check, shared by any
/// host. The network fetch (querying the GitHub Releases API) lives in the host
/// layer; this class only decides whether one dotted version string is newer than
/// another, so it can be unit-tested without hitting the network.
///
/// Named distinctly from the WPF host's internal <c>UpdateChecker</c> (which also
/// carries the v2 fetch code) so both can coexist during the port without an
/// ambiguous reference in the shared test project.
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="latest"/> is strictly newer than
    /// <paramref name="current"/>. Parses dotted numeric strings (e.g. "3.10",
    /// tolerating an optional leading 'v'); falls back to ordinal string comparison
    /// when parsing fails. Component comparison is numeric, so "3.10" &gt; "3.9".
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (TryParseVersion(latest, out Version? lv) && TryParseVersion(current, out Version? cv))
            return lv! > cv!;

        // Fall back to a case-insensitive ordinal compare so two unparseable-but-
        // equal strings (e.g. both empty) are never treated as an update.
        return string.Compare(latest ?? string.Empty, current ?? string.Empty,
                              StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Parses a dotted numeric version, tolerating a leading 'v'/'V' and a
    /// single-component value ("3" → "3.0"). Returns false for null/blank or
    /// non-numeric input.
    /// </summary>
    public static bool TryParseVersion(string? s, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        string t = s.Trim().TrimStart('v', 'V');
        // Version.TryParse needs at least two components; pad a bare "3" to "3.0".
        if (!t.Contains('.'))
            t += ".0";
        return Version.TryParse(t, out version);
    }
}
