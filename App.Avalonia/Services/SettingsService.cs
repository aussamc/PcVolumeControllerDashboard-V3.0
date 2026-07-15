using System.Text.Json;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Loads, holds, and persists the dashboard <see cref="DashboardSettings"/> for
/// the Avalonia host, wrapping the platform-agnostic Core
/// <see cref="SettingsRepository"/>. A single instance is shared via DI.
///
/// Every <see cref="Save"/> diffs the new state against the last-persisted snapshot and
/// logs each user-made change (discrete changes at Info, drag-style slider values at Debug
/// via <see cref="SettingsChangeLog"/>) so a shared log shows what was reconfigured.
/// </summary>
public sealed class SettingsService
{
    // Mirror the WPF host's constants: six channels; encoder sensitivity 0–500 %.
    private const int ChannelCount = 6;
    private const int MaxEncoderSensitivityPercent = 500;

    private readonly LogService _log;

    // Snapshot of the settings as of the last save/load — diffed against on the next save to
    // log what the user changed. Null until the first Load completes, so the migration save
    // that can run inside Load() doesn't emit a spurious change dump.
    private DashboardSettings? _lastLogged;

    public SettingsService(LogService log) => _log = log;

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
        // _lastLogged is still null here, so this save logs nothing (a migration isn't
        // a user change). Snapshot the pre-migration file first (no-op on a fresh install
        // — Backup early-returns when no file exists) so a bad migration is recoverable
        // from the setup_backups folder rather than silently overwriting the original.
        if (result.MigrationApplied)
        {
            SettingsRepository.Backup("premigration", _log.Log);
            Save();
        }

        _lastLogged = Clone(Settings);
    }

    /// <summary>Marks the first-run wizard as completed and persists the setting.</summary>
    public void MarkFirstRunComplete()
    {
        Settings.FirstRunWizardCompleted = true;
        Save();
    }

    /// <summary>
    /// Writes the current settings to disk, first logging any change from the last-saved
    /// snapshot (discrete → Info, drag-style sliders → Debug).
    /// </summary>
    public void Save()
    {
        LogChanges();
        SettingsRepository.Save(Settings, _log.Log);
        _lastLogged = Clone(Settings);
    }

    /// <summary>Resets all settings to factory defaults and persists them.</summary>
    public void Reset()
    {
        Settings = DashboardSettings.CreateDefault();
        _log.Info("Settings reset to defaults.", "Settings");
        SettingsRepository.Save(Settings, _log.Log);
        _lastLogged = Clone(Settings);
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
        // A wholesale replace: log one line rather than a field-by-field dump.
        _log.Info($"Settings imported from {path}.", "Settings");
        SettingsRepository.Save(Settings, _log.Log);
        _lastLogged = Clone(Settings);
        return true;
    }

    // Writes one log line per changed setting, at the level SettingsChangeLog assigns.
    private void LogChanges()
    {
        if (_lastLogged == null)
            return;

        foreach (SettingsChange change in SettingsChangeLog.Diff(_lastLogged, Settings))
        {
            if (change.Level == SettingsChangeLevel.Debug)
                _log.Debug(change.Description, "Settings");
            else
                _log.Info(change.Description, "Settings");
        }
    }

    // Independent deep copy (JSON round-trip) so the live Settings can be mutated in place
    // without disturbing the snapshot we diff the next save against.
    private static DashboardSettings Clone(DashboardSettings settings) =>
        JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(settings))
            ?? DashboardSettings.CreateDefault();
}
