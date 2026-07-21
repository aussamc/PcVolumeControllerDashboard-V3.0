using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>Outcome of a <see cref="UpdateInstaller.DownloadAsync"/> call.</summary>
public sealed class UpdateDownloadResult
{
    /// <summary>The verified file on disk, or null on failure.</summary>
    public string? FilePath { get; init; }

    /// <summary>Non-null when the download or verification failed.</summary>
    public string? ErrorMessage { get; init; }

    public bool Success => FilePath != null && ErrorMessage == null;
}

/// <summary>
/// v3.19 download-and-apply engine. Resolves the running <see cref="UpdatePlatform"/>,
/// downloads the release asset the pure <see cref="UpdateAssetSelector"/> picks into a
/// temp file, verifies its size and (when the API provides one) its SHA-256, then applies
/// it by launching the platform installer, the new AppImage, a pacman install on Arch, or
/// a .deb hand-off elsewhere on Linux. Applying an
/// installer that replaces the running files means the app must exit, so
/// <see cref="Apply"/> returns whether the caller should shut down; the caller wires that
/// to the desktop lifetime.
///
/// Scope is "download + one-click apply" (per the roadmap): the download can be automatic
/// (AutoApplyUpdates), but launching the installer is always an explicit user action — we
/// never silently run an installer / UAC or polkit prompt behind the user's back. macOS has no
/// signed artifact yet (<see cref="UpdatePlatform.Unsupported"/>) — callers fall back to
/// opening the release page.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly HttpClientHandler Handler = new() { AllowAutoRedirect = true };
    private static readonly System.Net.Http.HttpClient Http = new(Handler) { Timeout = TimeSpan.FromMinutes(10) };

    private readonly LogService _log;

    public UpdateInstaller(LogService log) => _log = log;

    /// <summary>The temp folder update installers are downloaded into.</summary>
    private static string DownloadDir => Path.Combine(Path.GetTempPath(), "PcVolumeController-update");

    /// <summary>
    /// Resolves how this build updates itself: Windows installer on the Windows TFM; on the
    /// shared TFM, the AppImage when running from one (the <c>APPIMAGE</c> env var is set),
    /// else the pacman package on Arch and its derivatives or the .deb on other Linux;
    /// Unsupported on macOS.
    /// </summary>
    public static UpdatePlatform DetectPlatform()
    {
#if WINDOWS
        return UpdatePlatform.Windows;
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Running from an AppImage wins regardless of distro: the app updates itself
            // by replacing that file, and the host package manager isn't involved.
            string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage))
                return UpdatePlatform.LinuxAppImage;

            return IsArchFamily() ? UpdatePlatform.LinuxArch : UpdatePlatform.LinuxDeb;
        }
        return UpdatePlatform.Unsupported; // macOS: no signed artifact yet
#endif
    }

    /// <summary>
    /// True on Arch and its derivatives, read from <c>/etc/os-release</c>.
    ///
    /// Distro rather than install-method detection, deliberately: someone who unpacked
    /// the tarball by hand on Arch is still better served by the pacman package than by
    /// a .deb. <c>ID_LIKE=arch</c> is what covers the derivatives (CachyOS, EndeavourOS,
    /// Manjaro, Garuda), since each sets its own <c>ID</c>.
    ///
    /// Everything that isn't Arch keeps falling through to the .deb, which is only
    /// correct for Debian/Ubuntu — an RPM distro has the same "wrong package" problem
    /// this method fixes for Arch, and needs its own artifact before it can be detected.
    /// </summary>
    private static bool IsArchFamily()
    {
        try
        {
            const string osRelease = "/etc/os-release";
            if (!File.Exists(osRelease)) return false;

            foreach (string line in File.ReadLines(osRelease))
            {
                if (TryReadOsReleaseValue(line, "ID", out string id) &&
                    id.Equals("arch", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (TryReadOsReleaseValue(line, "ID_LIKE", out string like) &&
                    like.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(t => t.Equals("arch", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch
        {
            // Unreadable os-release just means "not detected as Arch" — fall back to
            // the previous behaviour rather than failing the update check.
        }
        return false;
    }

    // os-release values may be bare or quoted (ID=arch / ID_LIKE="arch").
    private static bool TryReadOsReleaseValue(string line, string key, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(key + "=", StringComparison.Ordinal)) return false;

        value = line[(key.Length + 1)..].Trim().Trim('"', '\'');
        return value.Length > 0;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> to a temp file, reporting 0..1 progress, then
    /// verifies its size and — if the asset carries a sha256 digest — its SHA-256. Returns
    /// a failure result (never throws) on any network / integrity error; a mismatched file
    /// is deleted so a corrupt download is never launched.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(ReleaseAsset asset,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? path = null;
        try
        {
            path = Path.Combine(DownloadDir, SanitiseName(asset.Name));
            Directory.CreateDirectory(DownloadDir);

            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, asset.DownloadUrl))
            using (System.Net.Http.HttpResponseMessage response =
                   await Http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);

                await using Stream http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await http.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                        progress?.Report(Math.Clamp((double)read / total.Value, 0, 1));
                }
            }

            string? verifyError = Verify(path, asset);
            if (verifyError != null)
            {
                TryDelete(path);
                _log.Warn($"Update download rejected: {verifyError}", "Update");
                return new UpdateDownloadResult { ErrorMessage = verifyError };
            }

            _log.Info($"Update asset downloaded and verified: {asset.Name}.", "Update");
            PruneOldDownloads(path);
            return new UpdateDownloadResult { FilePath = path };
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            return new UpdateDownloadResult { ErrorMessage = "Download cancelled." };
        }
        catch (Exception ex)
        {
            TryDelete(path);
            _log.Error("Update download failed", ex, "Update");
            return new UpdateDownloadResult { ErrorMessage = ex.Message };
        }
    }

    // After a good download, delete any older installers left in the update folder so
    // they don't pile up in %TEMP% across releases (each release filename is version-
    // stamped, so a new download never overwrites the previous one — leaving only the
    // just-verified file). Best-effort: a locked file (e.g. an installer still running)
    // is skipped, never thrown; the whole pass is wrapped so a cleanup hiccup can't fail
    // an otherwise-good download.
    private void PruneOldDownloads(string keepPath) =>
        DeleteStale(Path.GetFileName(keepPath), "Pruned", "prune old");

    /// <summary>
    /// Clears the update-download temp folder at launch — sweeps an installer left behind
    /// by an already-applied update or an abandoned/partial download. Best-effort and
    /// never throws: a file still in use (e.g. the installer that just launched this app
    /// and hasn't exited yet) is skipped and swept on the next launch. Safe to call on any
    /// launch (first-run, normal, or <c>--safe</c>).
    /// </summary>
    public void SweepDownloads() => DeleteStale(keepFileName: string.Empty, "Swept", "sweep");

    // Shared delete pass over DownloadDir: removes every file except keepFileName (empty =
    // remove all). Best-effort per file; the whole pass is wrapped so cleanup never throws.
    private void DeleteStale(string keepFileName, string doneVerb, string failVerb)
    {
        try
        {
            if (!Directory.Exists(DownloadDir)) return;
            int removed = 0;
            foreach (string file in UpdateDownloadCleanup.SelectStale(
                         Directory.EnumerateFiles(DownloadDir), keepFileName))
            {
                try { File.Delete(file); removed++; }
                catch { /* best-effort — skip a locked/in-use file */ }
            }
            if (removed > 0)
                _log.Info($"{doneVerb} {removed} update download(s) from the temp folder.", "Update");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not {failVerb} update downloads: {ex.Message}", "Update");
        }
    }

    // Size must match (when known) and, if the API gave a sha256 digest, the file hash
    // must match it. A missing digest falls back to the size check alone.
    private static string? Verify(string path, ReleaseAsset asset)
    {
        var info = new FileInfo(path);
        if (asset.Size > 0 && info.Length != asset.Size)
            return $"size mismatch (expected {asset.Size:N0} bytes, got {info.Length:N0}).";

        if (AssetDigest.TryGetSha256(asset.Digest, out _))
        {
            string actual = ComputeSha256(path);
            if (!AssetDigest.Matches(asset.Digest, actual))
                return "SHA-256 checksum mismatch.";
        }
        return null;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>
    /// Launches the downloaded update for <paramref name="platform"/> and returns whether
    /// the app should now exit so the installer can replace the running files. Windows:
    /// runs the installer (shell-execute, so UAC elevates as configured). AppImage: makes
    /// the new file executable and launches it. .deb: hands off to the system package
    /// tool via <c>xdg-open</c> (no exit — the package manager handles it). Never throws;
    /// returns <c>false</c> (don't exit) on failure or an unsupported platform.
    /// </summary>
    public bool Apply(string filePath, UpdatePlatform platform)
    {
        try
        {
            switch (platform)
            {
                case UpdatePlatform.Windows:
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                    _log.Info("Launched the Windows installer; exiting so it can replace the app.", "Update");
                    return true;

                case UpdatePlatform.LinuxAppImage:
                    MakeExecutable(filePath);
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = false });
                    _log.Info("Launched the new AppImage; exiting the old instance.", "Update");
                    return true;

                case UpdatePlatform.LinuxDeb:
                    Process.Start(new ProcessStartInfo("xdg-open", QuoteArg(filePath)) { UseShellExecute = false });
                    _log.Info("Opened the .deb in the system package installer.", "Update");
                    return false; // hand-off — the running app can stay up

                case UpdatePlatform.LinuxArch:
                    // pacman needs root, and a GUI app has no terminal to prompt in.
                    // pkexec raises the desktop's polkit dialog and runs the install
                    // itself, so this genuinely installs rather than handing the file
                    // off somewhere. Not waited on: the password prompt is modal to the
                    // user, not to us, and blocking here would freeze the UI behind it.
                    Process.Start(new ProcessStartInfo("pkexec",
                        $"pacman -U --noconfirm {QuoteArg(filePath)}") { UseShellExecute = false });
                    _log.Info("Installing the Arch package via pkexec pacman -U.", "Update");
                    // Stay running: pacman replaces files under a live process safely on
                    // Linux, and exiting now would leave the polkit prompt orphaned with
                    // no way to report success. The user restarts to pick up the new build.
                    return false;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Update apply failed", ex, "Update");
            return false;
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static string QuoteArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string SanitiseName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "update.bin" : name;
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
