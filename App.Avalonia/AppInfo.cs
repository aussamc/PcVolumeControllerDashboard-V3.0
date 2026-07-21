namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Single source of truth for the user-facing dashboard version string. Referenced by
/// <c>MainWindow</c> (About / diagnostics / update check) and the first-run wizard's
/// auto-update page. On a version bump (standing rule #2) update this constant plus the
/// csproj <c>&lt;Version&gt;</c>/<c>&lt;AssemblyVersion&gt;</c>/<c>&lt;FileVersion&gt;</c>,
/// the README table, and the release notes.
/// </summary>
internal static class AppInfo
{
    public const string Version = "3.24.1";
}
