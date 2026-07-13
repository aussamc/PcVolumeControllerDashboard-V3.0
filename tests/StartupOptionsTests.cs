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
    public void Parse_UnrelatedArgs_LeaveDebugOff()
    {
        StartupOptions.Parse(new[] { "--safe", "debug", "-d" })
            .ForceDebugTab.Should().BeFalse();
    }
}
