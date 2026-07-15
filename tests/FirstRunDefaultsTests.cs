using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// v3.16 fresh-install defaults: tray/login toggles default on, encoder acceleration
/// default on, OLED default mode = Large Volume Number, and only ch1 = Master is bound
/// (ch2–ch6 start unassigned). Also guards that <see cref="SettingsRepository.Normalize"/>
/// does NOT retroactively flip these for existing users' saved settings.
/// </summary>
public sealed class FirstRunDefaultsTests
{
    private const int ChannelCount = 6;
    private const int MaxSensitivity = 500;

    [Fact]
    public void FreshDefaults_TrayAndLoginToggles_DefaultOn()
    {
        var settings = new DashboardSettings();

        settings.MinimizeToTray.Should().BeTrue();
        settings.StartMinimizedToTray.Should().BeTrue();
        settings.StartWithWindows.Should().BeTrue();
    }

    [Fact]
    public void FreshDefaults_EncoderAcceleration_DefaultOnMediumSmoothingOff()
    {
        var settings = new DashboardSettings();

        settings.AccelerationEnabled.Should().BeTrue();
        settings.AccelerationPreset.Should().Be(AccelerationPresets.Medium);
        settings.VolumeSmoothingEnabled.Should().BeFalse("smoothing stays off by default");
    }

    [Fact]
    public void FreshDefaults_UpdatePrefs_CheckOnApplyOff()
    {
        // v3.18: introduced for the wizard's auto-update page. Check on by default,
        // auto-apply off (opt-in). The v3.19 engine only reads these.
        var settings = new DashboardSettings();

        settings.AutoCheckForUpdates.Should().BeTrue();
        settings.AutoApplyUpdates.Should().BeFalse("auto-applying an installer is opt-in");
    }

    [Fact]
    public void FreshDefaults_OledDisplayMode_IsLargeVolume()
    {
        new DashboardSettings().OledDisplayMode.Should().Be(DisplayModes.LargeVolume);
        DashboardSettings.CreateDefault().OledDisplayMode.Should().Be(DisplayModes.LargeVolume);
    }

    [Fact]
    public void FreshDefaults_OnlyMasterBound_RestUnassigned()
    {
        ChannelSettings[] channels = DashboardSettings.CreateDefaultChannels();

        channels.Should().HaveCount(ChannelCount);
        channels[0].TargetKey.Should().Be("MASTER");
        channels[0].FriendlyName.Should().Be("Master");

        for (int i = 1; i < channels.Length; i++)
        {
            channels[i].TargetKey.Should().BeEmpty($"ch{i + 1} starts unassigned on a fresh install");
            channels[i].FriendlyName.Should().BeEmpty($"ch{i + 1} carries no binding name on a fresh install");
        }
    }

    [Fact]
    public void SuggestedChannelNames_MatchChannelCountAndUseVoiceChat()
    {
        DashboardSettings.SuggestedChannelNames.Should().HaveCount(ChannelCount);
        DashboardSettings.SuggestedChannelNames[0].Should().Be("Master");
        // ch4 suggestion is "Voice Chat", not "Discord" (item 2).
        DashboardSettings.SuggestedChannelNames[3].Should().Be("Voice Chat");
        DashboardSettings.SuggestedChannelNames.Should().NotContain("Discord");
    }

    [Fact]
    public void FreshDefaults_NoChannelStartsAsPool()
    {
        // A pool is comma-separated PROC keys; a fresh install must not auto-promote any
        // channel into multi-app pool mode (item 1).
        foreach (ChannelSettings ch in DashboardSettings.CreateDefaultChannels())
            ch.TargetKey.Should().NotContain(",", "no channel starts in multi-app pool mode");
    }

    [Fact]
    public void Normalize_DoesNotFlipExistingUsersTrayLoginPreferences()
    {
        // An existing user who deliberately turned these OFF must keep them off after load.
        var settings = new DashboardSettings
        {
            SettingsVersion = 8,
            MinimizeToTray = false,
            StartMinimizedToTray = false,
            StartWithWindows = false,
            AccelerationEnabled = false,
        };

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.MinimizeToTray.Should().BeFalse();
        settings.StartMinimizedToTray.Should().BeFalse();
        settings.StartWithWindows.Should().BeFalse();
        settings.AccelerationEnabled.Should().BeFalse();
    }

    [Fact]
    public void Normalize_BlankOledDisplayMode_FallsBackToLargeVolume()
    {
        var settings = new DashboardSettings { SettingsVersion = 8, OledDisplayMode = "" };

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.OledDisplayMode.Should().Be(DisplayModes.LargeVolume);
    }

    [Fact]
    public void Normalize_PreservesExistingUsersChosenOledMode()
    {
        var settings = new DashboardSettings
        {
            SettingsVersion = 8,
            OledDisplayMode = DisplayModes.AppNameAndVolume,
        };

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.OledDisplayMode.Should().Be(DisplayModes.AppNameAndVolume);
    }
}
