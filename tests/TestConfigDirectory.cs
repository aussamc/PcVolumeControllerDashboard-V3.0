using System.IO;
using System.Runtime.CompilerServices;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Assembly-wide guard that redirects the per-user config directory to a throwaway
/// temp folder before any test runs.
///
/// Several tests call the real <see cref="SettingsRepository.Save"/>/<see cref="SettingsRepository.Load"/>
/// entry points, which resolve their path from <c>%APPDATA%\PcVolumeController</c>. Without this
/// redirect a `dotnet test` run silently overwrote the developer's own live settings.json with a
/// test fixture (blank channels, a "Gaming" profile, SettingsVersion 7) — which presented as
/// "the update lost my settings" the next time the dashboard was launched. The redirect lives in
/// a module initializer rather than a fixture so it also covers any test added later.
/// </summary>
internal static class TestConfigDirectory
{
    [ModuleInitializer]
    internal static void Redirect() =>
        SettingsRepository.ConfigDirectoryOverride = NewTempDirectory();

    /// <summary>
    /// Points the config directory at a fresh temp folder for the lifetime of the returned
    /// scope, then restores the previous value and deletes the folder. Use in tests that
    /// exercise the real Save/Load path so they don't depend on (or leak into) each other.
    /// </summary>
    internal static Scope CreateScope() => new(NewTempDirectory());

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"PcVcDashTestConfig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        internal Scope(string directory)
        {
            _previous = SettingsRepository.ConfigDirectoryOverride;
            DirectoryPath = directory;
            SettingsRepository.ConfigDirectoryOverride = directory;
        }

        /// <summary>The temp config directory in force for this scope.</summary>
        internal string DirectoryPath { get; }

        public void Dispose()
        {
            SettingsRepository.ConfigDirectoryOverride = _previous;
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — a leftover temp folder must never fail a test.
            }
        }
    }
}
