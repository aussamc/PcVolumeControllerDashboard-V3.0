using System.Linq;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="SettingsChangeLog"/>, the pure diff that classifies dashboard
/// settings changes into Info (discrete) / Debug (drag-style slider) log lines (v3.19.4).
/// </summary>
public sealed class SettingsChangeLogTests
{
    private static DashboardSettings Fresh() => DashboardSettings.CreateDefault();

    [Fact]
    public void NoChange_ReturnsEmpty()
    {
        SettingsChangeLog.Diff(Fresh(), Fresh()).Should().BeEmpty();
    }

    [Fact]
    public void NullInputs_ReturnEmpty()
    {
        SettingsChangeLog.Diff(null, Fresh()).Should().BeEmpty();
        SettingsChangeLog.Diff(Fresh(), null).Should().BeEmpty();
    }

    [Fact]
    public void DiscreteToggle_IsInfo()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.StartWithWindows = !before.StartWithWindows;

        SettingsChange change = SettingsChangeLog.Diff(before, after).Should().ContainSingle().Subject;
        change.Level.Should().Be(SettingsChangeLevel.Info);
        change.Description.Should().Contain("StartWithWindows").And.Contain("->");
    }

    [Fact]
    public void ModeChange_IsInfo()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.ThemeMode = ThemeModes.Dark;

        SettingsChangeLog.Diff(before, after)
            .Should().ContainSingle(c => c.Level == SettingsChangeLevel.Info && c.Description.Contains("ThemeMode"));
    }

    [Fact]
    public void SliderValue_IsDebug()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.EncoderSensitivityPercent = before.EncoderSensitivityPercent + 25;

        SettingsChange change = SettingsChangeLog.Diff(before, after).Should().ContainSingle().Subject;
        change.Level.Should().Be(SettingsChangeLevel.Debug);
        change.Description.Should().Contain("EncoderSensitivityPercent");
    }

    [Fact]
    public void OverlaySliders_AreDebug()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.OverlayOpacity = 0.5;
        after.OverlayScale = 1.25;

        SettingsChangeLog.Diff(before, after)
            .Should().OnlyContain(c => c.Level == SettingsChangeLevel.Debug);
    }

    [Fact]
    public void ChannelTargetAssignment_IsInfo()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.Channels[2].TargetKey = "PROC:chrome";
        after.Channels[2].FriendlyName = "Chrome";

        var changes = SettingsChangeLog.Diff(before, after);
        changes.Should().OnlyContain(c => c.Level == SettingsChangeLevel.Info);
        changes.Should().Contain(c => c.Description.StartsWith("Ch3.TargetKey") && c.Description.Contains("PROC:chrome"));
        changes.Should().Contain(c => c.Description.StartsWith("Ch3.FriendlyName"));
    }

    [Fact]
    public void ChannelSensitivity_IsDebug()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.Channels[0].SensitivityPercent = 120;

        SettingsChange change = SettingsChangeLog.Diff(before, after).Should().ContainSingle().Subject;
        change.Level.Should().Be(SettingsChangeLevel.Debug);
        change.Description.Should().StartWith("Ch1.SensitivityPercent");
    }

    [Fact]
    public void IncidentalState_IsIgnored()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.WindowWidth = 999;
        after.WindowHeight = 555;
        after.AudioSplitterRatio = 0.7;
        after.LastComPort = "COM9";
        after.SelectedChannelIndex = 4;
        after.LastUpdateCheckUtc = System.DateTime.UtcNow;

        SettingsChangeLog.Diff(before, after).Should().BeEmpty();
    }

    [Fact]
    public void BoolChange_FormatsAsOnOff()
    {
        DashboardSettings before = Fresh();  // AutoApplyUpdates defaults off
        DashboardSettings after = Fresh();
        after.AutoApplyUpdates = true;

        SettingsChangeLog.Diff(before, after).Single().Description
            .Should().Be("AutoApplyUpdates: off -> on");
    }

    [Fact]
    public void MultipleChanges_AllReported()
    {
        DashboardSettings before = Fresh();
        DashboardSettings after = Fresh();
        after.MinimizeToTray = !before.MinimizeToTray;      // Info
        after.OledBrightnessPercent = 40;                    // Debug
        after.AudioBackendMode = AudioBackendModes.VoiceMeeter; // Info

        var changes = SettingsChangeLog.Diff(before, after);
        changes.Should().HaveCount(3);
        changes.Count(c => c.Level == SettingsChangeLevel.Info).Should().Be(2);
        changes.Count(c => c.Level == SettingsChangeLevel.Debug).Should().Be(1);
    }
}
