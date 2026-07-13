using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="StartupOptions"/> — command-line flag parsing for the
/// dashboard host (the <c>--debug</c> force-show-Debug-tab flag).
/// </summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void Parse_Null_ReturnsDefaults()
    {
        StartupOptions.Parse(null).ForceDebugTab.Should().BeFalse();
    }

    [Fact]
    public void Parse_Empty_ReturnsDefaults()
    {
        StartupOptions.Parse(System.Array.Empty<string>()).ForceDebugTab.Should().BeFalse();
    }

    [Fact]
    public void Parse_DebugFlag_SetsForceDebugTab()
    {
        StartupOptions.Parse(new[] { "--debug" }).ForceDebugTab.Should().BeTrue();
    }

    [Theory]
    [InlineData("--DEBUG")]
    [InlineData("--Debug")]
    [InlineData("  --debug  ")]
    public void Parse_DebugFlag_IsCaseInsensitiveAndTrimmed(string arg)
    {
        StartupOptions.Parse(new[] { arg }).ForceDebugTab.Should().BeTrue();
    }

    [Fact]
    public void Parse_DebugAmongOtherArgs_StillDetected()
    {
        StartupOptions.Parse(new[] { "--safe", "somefile", "--debug" })
            .ForceDebugTab.Should().BeTrue();
    }

    [Fact]
    public void Parse_UnrelatedArgs_LeaveFlagsOff()
    {
        StartupOptions o = StartupOptions.Parse(new[] { "debug", "safe", "-d", "-s" });
        o.ForceDebugTab.Should().BeFalse();
        o.SafeMode.Should().BeFalse();
    }

    // ── --safe (N1) ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Null_SafeModeDefaultsOff()
    {
        StartupOptions.Parse(null).SafeMode.Should().BeFalse();
    }

    [Fact]
    public void Parse_SafeFlag_SetsSafeMode()
    {
        StartupOptions.Parse(new[] { "--safe" }).SafeMode.Should().BeTrue();
    }

    [Theory]
    [InlineData("--SAFE")]
    [InlineData("  --safe  ")]
    public void Parse_SafeFlag_IsCaseInsensitiveAndTrimmed(string arg)
    {
        StartupOptions.Parse(new[] { arg }).SafeMode.Should().BeTrue();
    }

    [Fact]
    public void Parse_DebugOnly_LeavesSafeOff()
    {
        StartupOptions.Parse(new[] { "--debug" }).SafeMode.Should().BeFalse();
    }

    [Fact]
    public void Parse_DebugAndSafe_BothDetectedIndependently()
    {
        StartupOptions o = StartupOptions.Parse(new[] { "--debug", "--safe" });
        o.ForceDebugTab.Should().BeTrue();
        o.SafeMode.Should().BeTrue();
    }
}
