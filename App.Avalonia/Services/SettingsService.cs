using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Loads, holds, and persists the dashboard <see cref="DashboardSettings"/> for
/// the Avalonia host, wrapping the platform-agnostic Core
/// <see cref="SettingsRepository"/>. A single instance is shared via DI.
/// </summary>
public sealed class SettingsService
{
    // Mirror the WPF host's constants: six channels; encoder sensitivity 0–500 %.
    private const int ChannelCount = 6;
    private const int MaxEncoderSensitivityPercent = 500;

    /// <summary>The live settings object. Mutated in place, then persisted via <see cref="Save"/>.</summary>
    public DashboardSettings Settings { get; private set; } = DashboardSettings.CreateDefault();

    /// <summary>True if the settings file existed but could not be parsed on load.</summary>
    public bool WasCorrupt { get; private set; }

    /// <summary>
    /// True when the first-run wizard should be shown: either this is a genuinely
    /// fresh install (no settings file yet) or a previous wizard was never completed.
    /// The wizard sets <see cref="MarkFirstRunComplete"/> when the user finishes it.
    /// </summary>
    public bool IsFirstRun => !Settings.FirstRunWizardCompleted;

    /// <summary>Absolute path to the settings file (per-OS user config dir).</summary>
    public static string SettingsPath => SettingsRepository.GetPath();

    /// <summary>Reads settings from disk. Never throws — falls back to defaults.</summary>
    public void Load()
    {
        // A missing settings file means this is a genuine first launch. The Core
        // load returns fresh defaults in that case, but FirstRunWizardCompleted
        // defaults to true (so existing users are never re-prompted) — flip it here
        // so the wizard runs once on a brand-new install. Kept in the App layer so
        // Core and the still-shipping WPF host are untouched.
        bool fileExisted = File.Exists(SettingsPath);

        SettingsRepository.LoadResult result =
            SettingsRepository.Load(ChannelCount, MaxEncoderSensitivityPercent);

        Settings = result.Settings;
        WasCorrupt = result.WasCorrupt;

        if (!fileExisted)
            Settings.FirstRunWizardCompleted = false;

        // Persist if a schema migration ran so the on-disk file is brought current.
        if (result.MigrationApplied)
            Save();
    }

    /// <summary>Marks the first-run wizard as completed and persists the setting.</summary>
    public void MarkFirstRunComplete()
    {
        Settings.FirstRunWizardCompleted = true;
        Save();
    }

    /// <summary>Writes the current settings to disk.</summary>
    public void Save() => SettingsRepository.Save(Settings);

    /// <summary>Resets all settings to factory defaults and persists them.</summary>
    public void Reset()
    {
        Settings = DashboardSettings.CreateDefault();
        Save();
    }

    /// <summary>Writes the current settings to an arbitrary file (user export).</summary>
    public void ExportTo(string path) => SettingsRepository.ExportTo(Settings, path);

    /// <summary>
    /// Replaces the live settings with a normalised copy read from an arbitrary file
    /// (user import) and persists it. Returns false (leaving the current settings
    /// untouched) if the file is missing or unparseable.
    /// </summary>
    public bool ImportFrom(string path)
    {
        DashboardSettings? imported = SettingsRepository.ImportFrom(path, ChannelCount, MaxEncoderSensitivityPercent);
        if (imported == null)
            return false;

        Settings = imported;
        Save();
        return true;
    }
}
