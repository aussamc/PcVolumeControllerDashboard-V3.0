using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure helpers for the GitHub asset content digest (e.g. <c>sha256:abcd…</c>) used to
/// verify a downloaded update. Kept host-free so the parse/compare logic is unit-tested;
/// the host computes the actual file hash and calls <see cref="Matches"/>.
/// </summary>
public static class AssetDigest
{
    private const string Sha256Prefix = "sha256:";

    /// <summary>
    /// Extracts the 64-hex-char SHA-256 from a <c>sha256:…</c> digest string. Returns
    /// <c>false</c> for null/blank input, a non-sha256 algorithm, or a wrong-length hex —
    /// i.e. "there is no SHA-256 to verify against", so the caller falls back to the size
    /// check rather than failing the update.
    /// </summary>
    public static bool TryGetSha256(string? digest, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(digest))
            return false;

        string d = digest.Trim();
        if (!d.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string candidate = d[Sha256Prefix.Length..].Trim();
        if (candidate.Length != 64 || !IsHex(candidate))
            return false;

        hex = candidate;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="actualSha256Hex"/> matches the SHA-256 embedded in
    /// <paramref name="digest"/>. Comparison is case-insensitive. Returns <c>false</c> when
    /// the digest carries no usable SHA-256 (use <see cref="TryGetSha256"/> first to decide
    /// whether a hash check even applies).
    /// </summary>
    public static bool Matches(string? digest, string? actualSha256Hex)
    {
        if (!TryGetSha256(digest, out string expected) || string.IsNullOrWhiteSpace(actualSha256Hex))
            return false;
        return string.Equals(expected, actualSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                return false;
        }
        return true;
    }
}
