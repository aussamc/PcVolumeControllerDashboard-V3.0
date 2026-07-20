namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure, side-effect-free policy for deciding whether an executable path is stable
/// enough to record as a run-at-login entry.
///
/// The hosts re-sync their login-startup entry on every launch (so the entry follows
/// the app when it is moved or reinstalled), writing the *current* process path. That
/// is only correct when the process is running from where it actually lives. Two
/// launches run the app from a location that disappears shortly afterwards:
///
///   * the auto-updater stages a downloaded build under the temp directory and runs
///     it from there — swept by Storage Sense / the next update;
///   * a developer build runs out of <c>bin\Debug</c> / <c>bin\Release</c> — replaced
///     or emptied by the next rebuild.
///
/// Recording either path leaves a login entry pointing at an executable that no longer
/// exists, so at the next logon the OS silently launches nothing — and because the
/// next manual launch from the real install location rewrites the entry, the damage
/// repairs itself before anyone can inspect it. Hosts consult this policy before the
/// passive per-launch re-sync so a transient location leaves the existing (good) entry
/// untouched instead of overwriting it.
///
/// Deliberately narrow: it rejects only *known-transient* locations. A portable build
/// run from Downloads, a USB stick, or any other unusual-but-stable directory is a
/// legitimate user choice and is not treated as transient.
/// </summary>
public static class StartupPathPolicy
{
    /// <summary>
    /// True if <paramref name="exePath"/> is somewhere the executable is not expected
    /// to still be at the next logon (the temp directory, or a build output folder).
    /// A blank or unparseable path is treated as transient — there is nothing safe to
    /// record either way.
    /// </summary>
    /// <param name="exePath">The running process's executable path.</param>
    /// <param name="tempDirectory">
    /// The temp directory to test against (the caller passes <c>Path.GetTempPath()</c>;
    /// injected so the check is testable without touching the real environment).
    /// </param>
    public static bool IsTransientLocation(string? exePath, string? tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return true;

        string full;
        try { full = Path.GetFullPath(exePath); }
        catch { return true; }

        return IsUnder(full, tempDirectory) || IsBuildOutput(full);
    }

    /// <summary>True if <paramref name="path"/> sits inside <paramref name="directory"/>.</summary>
    private static bool IsUnder(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        string root;
        try { root = Path.GetFullPath(directory); }
        catch { return false; }

        // Compare with a trailing separator so "C:\Temp2\app.exe" isn't read as living
        // under "C:\Temp".
        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if the path runs through a <c>bin\Debug</c> or <c>bin\Release</c> pair.
    /// Matched on whole path segments so a directory merely *containing* the words
    /// (say "Robin Release Notes") isn't mistaken for a build output folder.
    /// </summary>
    private static bool IsBuildOutput(string path)
    {
        string[] segments = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (!segments[i].Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;

            if (segments[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                segments[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
