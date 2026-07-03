using System;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the cross-platform <see cref="UpdateCheck"/> version-comparison
/// helper used by the Avalonia host (distinct from the WPF host's UpdateChecker).
/// </summary>
public sealed class UpdateCheckTests
{
    // ── IsNewer ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3.10", "3.9",  true)]   // numeric compare: 10 > 9 (the port's real case)
    [InlineData("3.9",  "3.10", false)]  // 9 < 10
    [InlineData("3.9",  "3.9",  false)]  // same version
    [InlineData("4.0",  "3.9",  true)]   // major bump
    [InlineData("3.10", "3.10", false)]  // equal
    [InlineData("3.10.1", "3.10", true)] // patch bump
    [InlineData("3.10", "3.10.1", false)]// older than a patch
    [InlineData("2.44", "2.43", true)]   // parity with the WPF UpdateChecker cases
    public void IsNewer_ReturnsExpected(string latest, string current, bool expected)
    {
        UpdateCheck.IsNewer(latest, current).Should().Be(expected);
    }

    [Theory]
    [InlineData("v3.10", "3.9",  true)]  // tolerate a leading 'v' on the tag
    [InlineData("v3.9",  "v3.9", false)]
    [InlineData("V3.10", "3.9",  true)]  // upper-case 'V'
    public void IsNewer_ToleratesLeadingV(string latest, string current, bool expected)
    {
        UpdateCheck.IsNewer(latest, current).Should().Be(expected);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "3.9")]
    [InlineData(null, "3.9")]
    public void IsNewer_BlankLatest_IsNeverNewer(string? latest, string? current)
    {
        UpdateCheck.IsNewer(latest, current).Should().BeFalse();
    }

    // ── TryParseVersion ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3.9",   3, 9)]
    [InlineData("3.10",  3, 10)]
    [InlineData("v3.10", 3, 10)]   // leading 'v' stripped
    [InlineData("3",     3, 0)]    // single component padded
    [InlineData("3.10.1",3, 10)]   // major.minor read from a three-part version
    public void TryParseVersion_Valid_ReturnsParsed(string input, int major, int minor)
    {
        UpdateCheck.TryParseVersion(input, out Version? version).Should().BeTrue();
        version!.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-version")]
    public void TryParseVersion_Invalid_ReturnsFalse(string? input)
    {
        UpdateCheck.TryParseVersion(input, out Version? version).Should().BeFalse();
        version.Should().BeNull();
    }
}
