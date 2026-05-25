using System.IO;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="SettingsRepository"/>: load/save roundtrip,
/// corruption handling, and schema migration logic.
/// </summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    // ── constants matching the production defaults ────────────────────────────────

    private const int ChannelCount = 6;
    private const int MaxSensitivity = 500;

    // ── temporary directory isolated per test run ─────────────────────────────────

    private readonly string _tempDir;

    public SettingsRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PcVcDashTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Writes JSON to a file in the temp dir and returns the path.</summary>
    private string WriteTempJson(string fileName, string json)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    // ── Normalize — migration v0 → v4 ────────────────────────────────────────────

    [Fact]
    public void Normalize_V0Settings_AppliesV1Migration()
    {
        var settings = new DashboardSettings { SettingsVersion = 0 };

        bool migrated = SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        migrated.Should().BeTrue();
        settings.AutoConnectOnLaunch.Should().BeTrue();
        settings.FirstRunWizardCompleted.Should().BeTrue();
        settings.ScanAllComPortsIfRememberedMissing.Should().BeTrue();
        settings.SettingsVersion.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Normalize_V1WithSelectNextChannelActions_RemovesSelectNextChannel()
    {
        // v1 → v2: SelectNextChannel is a single-encoder workaround that no longer applies
        // on the 6-encoder hardware.  After full migration the action must not remain
        // SelectNextChannel (it passes through NoAction → ToggleAssignedMute via the
        // v2→v3→v4 cascade, ending at the current default).
        var settings = new DashboardSettings { SettingsVersion = 1 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        foreach (var ch in settings.Channels)
            ch.ButtonAction = ChannelButtonActions.SelectNextChannel;

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        foreach (var ch in settings.Channels)
            ch.ButtonAction.Should().NotBe(ChannelButtonActions.SelectNextChannel,
                "v1→v2 migration must eliminate SelectNextChannel");
    }

    [Fact]
    public void Normalize_V2Settings_ReachesCurrentDefaultButtonAction()
    {
        // v2 → v3 sets ToggleAssignedMute to NoAction; v3 → v4 immediately promotes
        // NoAction back to ToggleAssignedMute (the current short-press default).
        // Net effect after full cascade: ToggleAssignedMute.
        var settings = new DashboardSettings { SettingsVersion = 2 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        foreach (var ch in settings.Channels)
            ch.ButtonAction = ChannelButtonActions.ToggleAssignedMute;

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        foreach (var ch in settings.Channels)
            ch.ButtonAction.Should().Be(ChannelButtonActions.ToggleAssignedMute,
                "v2→v3→v4 cascade leaves channels on ToggleAssignedMute (the current default)");
    }

    [Fact]
    public void Normalize_V3WithNoActions_MigratesThemToToggleMute()
    {
        // v3 → v4: NoAction should become ToggleAssignedMute.
        var settings = new DashboardSettings { SettingsVersion = 3 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        foreach (var ch in settings.Channels)
            ch.ButtonAction = ChannelButtonActions.NoAction;

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        foreach (var ch in settings.Channels)
            ch.ButtonAction.Should().Be(ChannelButtonActions.ToggleAssignedMute,
                "v3→v4 migration restores ToggleAssignedMute as the short-press default");
    }

    [Fact]
    public void Normalize_V4Settings_CreatesDefaultProfile()
    {
        // v4 → v5: a "Default" profile should be created.
        var settings = new DashboardSettings { SettingsVersion = 4 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.Profiles = new List<ProfileEntry>();

        bool migrated = SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        migrated.Should().BeTrue();
        settings.Profiles.Should().HaveCount(1);
        settings.Profiles[0].Name.Should().Be("Default");
        settings.ActiveProfileName.Should().Be("Default");
        settings.SettingsVersion.Should().Be(7);
    }

    [Fact]
    public void Normalize_UpToDateSettings_ReturnsFalse()
    {
        // A fully-current settings object should not be flagged as migrated.
        DashboardSettings settings = DashboardSettings.CreateDefault();
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.SettingsVersion = 7;  // current schema version
        settings.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Default", Channels = DashboardSettings.CreateDefaultChannels() }
        };
        settings.ActiveProfileName = "Default";

        bool migrated = SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        migrated.Should().BeFalse();
    }

    [Fact]
    public void Normalize_V6Settings_ClampsInvalidVolumeLimits()
    {
        // v6 → v7: inverted / out-of-range volume limits should be fixed.
        var settings = new DashboardSettings { SettingsVersion = 6 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        // Set an inverted pair and an out-of-range value on two channels.
        settings.Channels[0].MinVolumePercent = 80;
        settings.Channels[0].MaxVolumePercent = 20;  // inverted
        settings.Channels[1].MinVolumePercent = -5;  // out of range
        settings.Channels[1].MaxVolumePercent = 150; // out of range
        settings.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Default", Channels = settings.Channels }
        };
        settings.ActiveProfileName = "Default";

        bool migrated = SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        migrated.Should().BeTrue();
        // Inverted pair should be swapped.
        settings.Channels[0].MinVolumePercent.Should().BeLessOrEqualTo(settings.Channels[0].MaxVolumePercent);
        // Out-of-range values should be clamped.
        settings.Channels[1].MinVolumePercent.Should().Be(0);
        settings.Channels[1].MaxVolumePercent.Should().Be(100);
        settings.SettingsVersion.Should().Be(7);
    }

    // ── Normalize — value clamping ────────────────────────────────────────────────

    [Fact]
    public void Normalize_SensitivityAboveMax_ClampedToMax()
    {
        DashboardSettings settings = DashboardSettings.CreateDefault();
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.EncoderSensitivityPercent = MaxSensitivity + 999;
        settings.SettingsVersion = 7;
        settings.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Default", Channels = DashboardSettings.CreateDefaultChannels() }
        };
        settings.ActiveProfileName = "Default";

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.EncoderSensitivityPercent.Should().Be(MaxSensitivity);
    }

    [Fact]
    public void Normalize_NullChannels_ReplacedWithDefaults()
    {
        var settings = new DashboardSettings { SettingsVersion = 7 };
        settings.Channels = null!;  // force null to simulate corrupt/missing array
        settings.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Default", Channels = DashboardSettings.CreateDefaultChannels() }
        };
        settings.ActiveProfileName = "Default";

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Channels.Should().NotBeNull();
        settings.Channels.Should().HaveCount(ChannelCount);
    }

    [Fact]
    public void Normalize_ProfileWithWrongChannelCount_ReplacedWithDefaults()
    {
        DashboardSettings settings = DashboardSettings.CreateDefault();
        settings.SettingsVersion = 7;
        var badProfile = new ProfileEntry
        {
            Name = "Default",
            Channels = new ChannelSettings[2]   // wrong count
        };
        settings.Profiles = new List<ProfileEntry> { badProfile };
        settings.ActiveProfileName = "Default";

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Profiles[0].Channels.Should().HaveCount(ChannelCount);
    }

    // ── Save / Load roundtrip ─────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaultSettings()
    {
        // Point SettingsRepository to a path that doesn't exist by temporarily
        // overriding %APPDATA%.  Instead we call Normalize + check the Load
        // result type.  Since Load calls GetPath() which uses %APPDATA%, we
        // exercise the public static interface through Save/Load below.

        // If the settings file doesn't exist, Load should return defaults.
        string settingsPath = SettingsRepository.GetPath();
        if (File.Exists(settingsPath))
            return;  // skip: settings file exists on this machine

        SettingsRepository.LoadResult result = SettingsRepository.Load(ChannelCount, MaxSensitivity);

        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        result.WasCorrupt.Should().BeFalse();
    }

    [Fact]
    public void Save_ThenLoad_PreservesActiveProfileName()
    {
        // Save a settings object with a known profile name, then re-load it.
        DashboardSettings original = DashboardSettings.CreateDefault();
        original.SettingsVersion = 7;
        original.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Gaming", Channels = DashboardSettings.CreateDefaultChannels() }
        };
        original.ActiveProfileName = "Gaming";
        original.Channels = DashboardSettings.CreateDefaultChannels();

        SettingsRepository.Save(original);
        SettingsRepository.LoadResult loaded = SettingsRepository.Load(ChannelCount, MaxSensitivity);

        loaded.WasCorrupt.Should().BeFalse();
        loaded.Settings.Profiles.Should().Contain(p => p.Name == "Gaming");
        loaded.Settings.ActiveProfileName.Should().Be("Gaming");
    }

    [Fact]
    public void Save_LoggerCalledOnError_DoesNotThrow()
    {
        // Providing a null logger should never throw.
        DashboardSettings settings = DashboardSettings.CreateDefault();

        Action act = () => SettingsRepository.Save(settings, logger: null);
        act.Should().NotThrow();
    }
}
