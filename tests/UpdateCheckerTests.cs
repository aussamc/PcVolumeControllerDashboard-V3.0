using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateChecker"/> version comparison logic.
/// These tests exercise the internal helpers directly without hitting the network.
/// </summary>
public sealed class UpdateCheckerTests
{
    // ── IsVersionNewer ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.44", "2.43", true)]   // minor bump → newer
    [InlineData("2.43", "2.44", false)]  // older than current
    [InlineData("2.43", "2.43", false)]  // same version
    [InlineData("3.0",  "2.43", true)]   // major bump
    [InlineData("2.10", "2.9",  true)]   // numeric comparison: 10 > 9 (not string "10" < "9")
    [InlineData("2.9",  "2.10", false)]  // numeric comparison: 9 < 10
    [InlineData("2.0",  "2.0",  false)]  // equal two-part versions
    [InlineData("2.1.0","2.0.9",true)]   // three-part version
    public void IsVersionNewer_ReturnsExpected(string latest, string current, bool expected)
    {
        bool result = UpdateChecker.IsVersionNewer(latest, current);
        result.Should().Be(expected);
    }

    [Fact]
    public void IsVersionNewer_BothEmpty_ReturnsFalse()
    {
        // Empty strings are equal — not newer.
        UpdateChecker.IsVersionNewer(string.Empty, string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsVersionNewer_LatestEmpty_ReturnsFalse()
    {
        UpdateChecker.IsVersionNewer(string.Empty, "2.43").Should().BeFalse();
    }

    // ── TryParseVersion ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.43",  true,  2, 43)]
    [InlineData("2.0",   true,  2, 0)]
    [InlineData("3.1.2", true,  3, 1)]   // System.Version parses major.minor from "3.1.2"
    [InlineData("2",     true,  2, 0)]   // single-part padded to "2.0"
    public void TryParseVersion_ValidInput_ReturnsTrueAndParsedVersion(
        string input, bool expectedOk, int expectedMajor, int expectedMinor)
    {
        bool ok = UpdateChecker.TryParseVersion(input, out Version? version);

        ok.Should().Be(expectedOk);
        if (expectedOk)
        {
            version.Should().NotBeNull();
            version!.Major.Should().Be(expectedMajor);
            version.Minor.Should().Be(expectedMinor);
        }
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("abc.def")]
    public void TryParseVersion_InvalidInput_ReturnsFalse(string input)
    {
        bool ok = UpdateChecker.TryParseVersion(input, out Version? version);

        ok.Should().BeFalse();
        version.Should().BeNull();
    }
}
