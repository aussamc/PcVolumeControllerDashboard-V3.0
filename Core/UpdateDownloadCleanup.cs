using System;
using System.Collections.Generic;
using System.IO;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure decision for pruning the update-download temp folder. The host downloads each
/// update installer into a dedicated temp dir and never overwrites an older version's
/// file (release filenames are version-stamped), so without pruning they accumulate.
/// This picks which files to delete to leave only the current download; kept host-free
/// so the choice is unit-tested — the host enumerates the dir and does the deletes.
/// </summary>
public static class UpdateDownloadCleanup
{
    /// <summary>
    /// From the file paths currently in the download folder, returns those to delete so
    /// only <paramref name="keepFileName"/> (the just-verified download) remains. Matches
    /// on the file name only, case-insensitively; the kept file — and anything else with
    /// the same name — is excluded. A blank <paramref name="keepFileName"/> keeps nothing,
    /// i.e. every file is returned as stale.
    /// </summary>
    public static IReadOnlyList<string> SelectStale(IEnumerable<string> files, string keepFileName)
    {
        ArgumentNullException.ThrowIfNull(files);

        var stale = new List<string>();
        foreach (string file in files)
        {
            if (string.IsNullOrEmpty(file))
                continue;
            if (!string.IsNullOrEmpty(keepFileName) &&
                string.Equals(Path.GetFileName(file), keepFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            stale.Add(file);
        }
        return stale;
    }
}
