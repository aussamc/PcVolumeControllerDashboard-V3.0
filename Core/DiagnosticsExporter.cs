using System.IO;
using System.IO.Compression;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Builds a diagnostics zip (settings snapshot + log files + an info note) for bug
/// reports. Pure file I/O with explicit paths so it's host-agnostic and unit
/// testable; the host supplies the real config-dir paths. Mirrors the WPF host's
/// one-click diagnostics export.
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>
    /// Creates <paramref name="outputZipPath"/> containing the settings file (if
    /// present), every file in the logs directory (if present, under <c>logs/</c>),
    /// and an <c>info.txt</c> with <paramref name="infoText"/>. Overwrites an
    /// existing zip. Returns the output path.
    /// </summary>
    public static string Create(string outputZipPath, string? settingsPath, string? logsDirectory, string infoText)
    {
        string? parent = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(outputZipPath))
            File.Delete(outputZipPath);

        using var zip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

        // Info note (always present, so the archive is never empty).
        ZipArchiveEntry info = zip.CreateEntry("info.txt");
        using (var writer = new StreamWriter(info.Open()))
            writer.Write(infoText ?? string.Empty);

        // Settings snapshot.
        if (!string.IsNullOrEmpty(settingsPath) && File.Exists(settingsPath))
            AddFile(zip, settingsPath, Path.GetFileName(settingsPath));

        // Log files, under a logs/ folder.
        if (!string.IsNullOrEmpty(logsDirectory) && Directory.Exists(logsDirectory))
        {
            foreach (string file in Directory.GetFiles(logsDirectory))
                AddFile(zip, file, $"logs/{Path.GetFileName(file)}");
        }

        return outputZipPath;
    }

    // Copies a file into the archive via a stream so a file held open for writing
    // elsewhere (e.g. the live log) can still be read with a shared lock.
    private static void AddFile(ZipArchive zip, string sourcePath, string entryName)
    {
        try
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            // Skip a file we genuinely can't read; the rest of the archive still builds.
        }
    }
}
