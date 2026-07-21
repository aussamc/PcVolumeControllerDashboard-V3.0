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
        settings.SettingsVersion.Should().Be(9);
    }

    [Fact]
    public void Normalize_UpToDateSettings_ReturnsFalse()
    {
        // A fully-current settings object should not be flagged as migrated.
        DashboardSettings settings = DashboardSettings.CreateDefault();
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.SettingsVersion = 9;  // current schema version
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
        settings.SettingsVersion.Should().Be(9);
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

    // ── Regression: a stale active profile must never override the real mapping ─────

    // The Avalonia host (the single UI) reads and writes the top-level Channels array;
    // the active profile's Channels is only a passive mirror. If the two diverge — e.g.
    // an older/WPF-era file whose profile copy is stale — Normalize must keep the
    // top-level mapping and re-sync the profile to it, NOT the reverse. Regressing this
    // reset every channel assignment to default the first time a new build read the file
    // (i.e. right after an update).
    [Fact]
    public void Normalize_StaleProfile_TopLevelChannelMappingWins()
    {
        var settings = new DashboardSettings { SettingsVersion = 8 };  // already migrated

        // The user's real mapping lives on the top-level array.
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.Channels[1].TargetKey = "PROC:chrome";

        // A stale active profile still carries the default (empty) assignment.
        settings.Profiles = new List<ProfileEntry>
        {
            new ProfileEntry { Name = "Default", Channels = DashboardSettings.CreateDefaultChannels() }
        };
        settings.ActiveProfileName = "Default";

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        // The edited mapping survives, and the profile is re-synced to it.
        settings.Channels[1].TargetKey.Should().Be("PROC:chrome");
        settings.Profiles[0].Channels[1].TargetKey.Should().Be("PROC:chrome");
    }

    // ── Single-target channels must not be seeded into a "pool" (item #4) ───────────

    // A fresh install's Master is a single assigned target (TargetKey="MASTER") with an
    // empty pool list. Normalize must not auto-seed TargetKeys from TargetKey — doing so
    // made Master render as a multi-app "(pool)".
    [Fact]
    public void Normalize_FreshSingleTargetChannel_LeavesPoolEmpty()
    {
        var settings = DashboardSettings.CreateDefault();  // ch1 = MASTER, empty TargetKeys

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Channels[0].TargetKey.Should().Be("MASTER");
        settings.Channels[0].TargetKeys.Should().BeEmpty("a single target must not become a pool");
    }

    // Existing installs already have TargetKeys seeded with the single TargetKey (the
    // v7→v8 artefact). The v8→v9 migration must clear that 1-entry duplicate so the
    // channel is a clean single target again.
    [Fact]
    public void Normalize_UnseedsSingleTargetPoolFromExistingFile()
    {
        var settings = new DashboardSettings { SettingsVersion = 8 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.Channels[0].TargetKey = "MASTER";
        settings.Channels[0].TargetKeys = new List<string> { "MASTER" };  // seeded duplicate

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Channels[0].TargetKeys.Should().BeEmpty("a 1-entry list duplicating TargetKey is not a pool");
        settings.Channels[0].TargetKey.Should().Be("MASTER");
    }

    // A genuine multi-app pool (2+ distinct entries) must be preserved untouched.
    [Fact]
    public void Normalize_PreservesGenuineMultiAppPool()
    {
        var settings = new DashboardSettings { SettingsVersion = 8 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.Channels[1].TargetKey = "PROC:chrome";
        settings.Channels[1].TargetKeys = new List<string> { "PROC:chrome", "PROC:firefox" };

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Channels[1].TargetKeys.Should().Equal("PROC:chrome", "PROC:firefox");
    }

    // ── Save / Load roundtrip ─────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaultSettings()
    {
        // Runs against an empty temp config directory, so the settings file is
        // genuinely absent and Load must fall back to defaults.
        using TestConfigDirectory.Scope scope = TestConfigDirectory.CreateScope();
        File.Exists(SettingsRepository.GetPath()).Should().BeFalse();

        SettingsRepository.LoadResult result = SettingsRepository.Load(ChannelCount, MaxSensitivity);

        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        result.WasCorrupt.Should().BeFalse();
    }

    [Fact]
    public void Save_ThenLoad_PreservesActiveProfileName()
    {
        // Writes through the real Save/Load entry points — pinned to a temp config
        // directory so it can never touch the developer's live settings.json.
        using TestConfigDirectory.Scope scope = TestConfigDirectory.CreateScope();

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
        using TestConfigDirectory.Scope scope = TestConfigDirectory.CreateScope();
        DashboardSettings settings = DashboardSettings.CreateDefault();

        Action act = () => SettingsRepository.Save(settings, logger: null);
        act.Should().NotThrow();
    }

    // ── Config-directory redirection ──────────────────────────────────────────────

    [Fact]
    public void ConfigDirectory_IsRedirected_ForTheWholeTestRun()
    {
        // Regression guard: the module initializer must have moved the config
        // directory off the real per-user location before any test ran. Without it,
        // the Save/Load tests above overwrite the developer's live settings.json —
        // the actual cause of "the update wiped my channel assignments".
        string liveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PcVolumeController");

        SettingsRepository.ConfigDirectoryOverride.Should().NotBeNullOrWhiteSpace();
        SettingsRepository.GetConfigDirectory().Should().NotBe(liveDir);
        SettingsRepository.GetPath().Should().NotBe(Path.Combine(liveDir, "settings.json"));
        SettingsRepository.GetBackupDirectory().Should().NotBe(Path.Combine(liveDir, "setup_backups"));
    }

    [Fact]
    public void ConfigDirectory_HonoursEnvironmentVariable_WhenNoOverrideSet()
    {
        string? savedOverride = SettingsRepository.ConfigDirectoryOverride;
        string? savedEnv = Environment.GetEnvironmentVariable(SettingsRepository.ConfigDirectoryEnvVar);
        try
        {
            SettingsRepository.ConfigDirectoryOverride = null;
            Environment.SetEnvironmentVariable(SettingsRepository.ConfigDirectoryEnvVar, _tempDir);

            SettingsRepository.GetConfigDirectory().Should().Be(_tempDir);
            SettingsRepository.GetPath().Should().Be(Path.Combine(_tempDir, "settings.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsRepository.ConfigDirectoryEnvVar, savedEnv);
            SettingsRepository.ConfigDirectoryOverride = savedOverride;
        }
    }

    // ── Channel linking ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_LinkedGroupId_IsPreservedThroughMigration()
    {
        // LinkedGroupId has no migration gate — it should survive a full Normalize
        // pass regardless of the incoming SettingsVersion.
        var settings = new DashboardSettings { SettingsVersion = 0 };
        settings.Channels = DashboardSettings.CreateDefaultChannels();
        settings.Channels[0].LinkedGroupId = "music";
        settings.Channels[2].LinkedGroupId = "music";

        SettingsRepository.Normalize(settings, ChannelCount, MaxSensitivity);

        settings.Channels[0].LinkedGroupId.Should().Be("music");
        settings.Channels[2].LinkedGroupId.Should().Be("music");
        settings.Channels[1].LinkedGroupId.Should().BeEmpty();
        settings.Channels[3].LinkedGroupId.Should().BeEmpty();
    }

    [Fact]
    public void Normalize_LinkedGroupId_DefaultIsEmpty()
    {
        // Freshly constructed ChannelSettings should default to no link group.
        var ch = new ChannelSettings();
        ch.LinkedGroupId.Should().BeEmpty();
    }
}
