using System;
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
}
