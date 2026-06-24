using FluentAssertions;
using PcVolumeControllerDashboard.Platform.Windows;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the pure key-handling helpers on <see cref="VoiceMeeterBackend"/>.
/// (The native VoiceMeeter API paths require a running VoiceMeeter and are not
/// exercised here.)
/// </summary>
public sealed class VoiceMeeterBackendTests
{
    [Theory]
    [InlineData("VM_STRIP:0", true)]
    [InlineData("VM_STRIP:7", true)]
    [InlineData("VM_BUS:0", true)]
    [InlineData("vm_bus:2", true)]   // case-insensitive
    [InlineData("PROC:chrome", false)]
    [InlineData("MASTER", false)]
    [InlineData("MIC_INPUT", false)]
    [InlineData(null, false)]
    public void IsVoiceMeeterKey_RecognisesVmKeys(string? key, bool expected)
    {
        VoiceMeeterBackend.IsVoiceMeeterKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData("VM_STRIP:0", "Strip 1")]   // 0-based key → 1-based display
    [InlineData("VM_STRIP:4", "Strip 5")]
    [InlineData("VM_BUS:0", "Bus 1")]
    [InlineData("VM_BUS:2", "Bus 3")]
    public void MakeDisplayLabel_ConvertsToOneBasedLabel(string key, string expected)
    {
        VoiceMeeterBackend.MakeDisplayLabel(key).Should().Be(expected);
    }

    [Fact]
    public void MakeDisplayLabel_ReturnsKeyForNonVmKey()
    {
        VoiceMeeterBackend.MakeDisplayLabel("PROC:chrome").Should().Be("PROC:chrome");
    }
}
