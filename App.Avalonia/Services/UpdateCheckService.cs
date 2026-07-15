using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>Outcome of a software update check.</summary>
public sealed class UpdateCheckResult
{
    /// <summary>True when a strictly newer release than the running build was found.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The latest release version (tag with any leading 'v' stripped).</summary>
    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>Web URL to the release (or the releases page as a fallback).</summary>
    public string ReleaseUrl { get; init; } = ReleasesPage;

    /// <summary>True when the repo has no published releases yet (HTTP 404) — a normal state.</summary>
    public bool NoReleasesPublished { get; init; }

    /// <summary>Non-null when the check could not complete (network/timeout/parse).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The downloadable files attached to the latest release (installer / portable exe /
    /// .deb / AppImage), for the v3.19 download-and-apply engine. Empty when the check
    /// failed or the release has no assets.
    /// </summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();

    internal const string OwnerRepo = "aussamc/PcVolumeControllerDashboard-V3.0";
    internal const string ReleasesPage = "https://github.com/" + OwnerRepo + "/releases";
}

/// <summary>
/// Queries the GitHub Releases API for a newer version of the Avalonia dashboard
/// and compares it to the running build via the pure Core <see cref="UpdateCheck"/>
/// comparator. Cross-platform (plain HTTPS) and manual (user-triggered) — never
/// throws; all failures come back through <see cref="UpdateCheckResult.ErrorMessage"/>.
///
/// The Avalonia counterpart of the WPF host's internal UpdateChecker, retargeted at
/// the v3 repository.
/// </summary>
public sealed class UpdateCheckService
{
    private const string ApiUrl = "https://api.github.com/repos/" + UpdateCheckResult.OwnerRepo + "/releases/latest";

    // Static so the socket is reused across checks.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>
    /// Queries the latest release and compares its tag with <paramref name="currentVersion"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            // Per-request message so the static client isn't mutated (concurrency-safe)
            // and the User-Agent (required by the GitHub API) reflects this build.
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd($"PcVolumeControllerDashboard-Avalonia/{currentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            // 404 = no releases published yet; a normal state, not an error.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult { NoReleasesPublished = true };

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult { ErrorMessage = $"GitHub API returned HTTP {(int)response.StatusCode}." };

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement))
                return new UpdateCheckResult { ErrorMessage = "Unexpected API response (missing tag_name)." };

            string tag = tagElement.GetString() ?? string.Empty;
            string latestVersion = tag.TrimStart('v', 'V');

            string releaseUrl = UpdateCheckResult.ReleasesPage;
            if (doc.RootElement.TryGetProperty("html_url", out JsonElement urlElement))
                releaseUrl = urlElement.GetString() ?? UpdateCheckResult.ReleasesPage;

            return new UpdateCheckResult
            {
                UpdateAvailable = UpdateCheck.IsNewer(latestVersion, currentVersion),
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl,
                Assets = ParseAssets(doc.RootElement),
            };
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult { ErrorMessage = $"Network error: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult { ErrorMessage = "Request timed out." };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Parses the release's <c>assets[]</c> into <see cref="ReleaseAsset"/> records
    /// (name / download URL / size / optional sha256 digest). Tolerant: any asset missing
    /// a name or URL is skipped, and a missing <c>assets</c> array yields an empty list.
    /// </summary>
    private static IReadOnlyList<ReleaseAsset> ParseAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return Array.Empty<ReleaseAsset>();

        var list = new List<ReleaseAsset>(assets.GetArrayLength());
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                continue;

            long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long parsed) ? parsed : 0;
            string? digest = a.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;

            list.Add(new ReleaseAsset { Name = name, DownloadUrl = url, Size = size, Digest = digest });
        }
        return list;
    }
}
