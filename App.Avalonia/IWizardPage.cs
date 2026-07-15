namespace PcVolumeControllerDashboard.App;

/// <summary>
/// A self-contained first-run wizard page. Implemented as a <c>UserControl</c> that binds
/// to the shared <see cref="Services.SettingsService"/> (and any other runtime services it
/// needs, passed via its constructor). The <see cref="FirstRunWizard"/> host inserts the
/// page into the step sequence based on the chosen stream (Quick / Advanced) and drives it
/// through these hooks as the user navigates.
///
/// Pages persist directly to settings on change (mirroring the Setup tab's pattern), so
/// <see cref="OnLeave"/> is only needed for pages that batch their writes. Keep a designer
/// parameterless constructor alongside the real one, as the rest of the app's controls do.
/// </summary>
public interface IWizardPage
{
    /// <summary>Header title shown for this step (e.g. "Choose a theme").</summary>
    string Title { get; }

    /// <summary>
    /// Called when this page becomes the active step. Use it to refresh live data
    /// (e.g. re-read a setting, re-enumerate audio backends) before the user sees it.
    /// </summary>
    void OnShow();

    /// <summary>
    /// Called when navigating away from this page (Next/Back). Persist anything not
    /// already saved on change. Safe to leave empty for save-on-change pages.
    /// </summary>
    void OnLeave();
}
