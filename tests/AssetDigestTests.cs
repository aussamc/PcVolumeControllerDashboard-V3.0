using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="AssetDigest"/>, the pure parse/compare for the GitHub asset
/// <c>sha256:…</c> content digest used to verify a downloaded update (v3.19).
/// </summary>
public sealed class AssetDigestTests
{
    private const string Hex = "8f321b4c52c8d1031e4d5c5b33a47305f43f4514588055859649a3951e81aee4";
    private const string Digest = "sha256:" + Hex;

    // ── TryGetSha256 ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryGetSha256_ValidDigest_ExtractsHex()
    {
        AssetDigest.TryGetSha256(Digest, out string hex).Should().BeTrue();
        hex.Should().Be(Hex);
    }

    [Fact]
    public void TryGetSha256_UpperCasePrefix_Accepted()
    {
        AssetDigest.TryGetSha256("SHA256:" + Hex, out string hex).Should().BeTrue();
        hex.Should().Be(Hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("md5:abcdef")]                 // wrong algorithm
    [InlineData("sha256:tooshort")]            // wrong length
    [InlineData("sha256:zz321b4c52c8d1031e4d5c5b33a47305f43f4514588055859649a3951e81aeez")] // non-hex
    public void TryGetSha256_Invalid_ReturnsFalse(string? digest)
    {
        AssetDigest.TryGetSha256(digest, out string hex).Should().BeFalse();
        hex.Should().BeEmpty();
    }

    // ── Matches ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Matches_SameHash_IsTrue()
    {
        AssetDigest.Matches(Digest, Hex).Should().BeTrue();
    }

    [Fact]
    public void Matches_CaseInsensitive()
    {
        AssetDigest.Matches(Digest, Hex.ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public void Matches_DifferentHash_IsFalse()
    {
        AssetDigest.Matches(Digest, "0000000000000000000000000000000000000000000000000000000000000000")
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_NoUsableDigest_IsFalse()
    {
        AssetDigest.Matches(null, Hex).Should().BeFalse();
        AssetDigest.Matches("md5:abc", Hex).Should().BeFalse();
    }
}
