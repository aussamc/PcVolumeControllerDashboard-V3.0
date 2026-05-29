using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcVolumeControllerDashboard;

/// <summary>
/// Checks the GitHub Releases API for a newer version of the dashboard.
/// Uses a static HttpClient so the socket is reused across calls.
/// </summary>
internal static class UpdateChecker
{
    private const string OwnerRepo = "aussamc/PcVolumeControllerDashboard";
    public const string ReleasesUrl = "https://github.com/" + OwnerRepo + "/releases";
    private const string ApiUrl = "https://api.github.com/repos/" + OwnerRepo + "/releases/latest";

    // ── Result ──────────────────────────────────────────────────────────────────

    public sealed class UpdateResult
    {
        public bool UpdateAvailable { get; init; }
        public string LatestVersion { get; init; } = string.Empty;
        public string ReleaseUrl { get; init; } = ReleasesUrl;
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// True when the GitHub repo has no published releases yet (HTTP 404).
        /// This is a normal state, not an error or an "update available" result.
        /// </summary>
        public bool NoReleasesPublished { get; init; }
    }

    // ── HTTP client (static — reuse socket) ─────────────────────────────────────

    private static readonly HttpClient _httpClient;

    static UpdateChecker()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    // ── Public API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the GitHub Releases API and compares the latest tag with
    /// <paramref name="currentVersion"/>.  Never throws — all errors are
    /// returned via <see cref="UpdateResult.ErrorMessage"/>.
    /// </summary>
    public static async Task<UpdateResult> CheckAsync(string currentVersion)
    {
        try
        {
            // Build a per-request message so the User-Agent reflects the current version
            // and the static HttpClient is not mutated (safe for concurrent calls).
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd($"PcVolumeControllerDashboard/{currentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // 404 means no releases have been published yet — that is a normal
            // state, not an error, so surface a friendly message instead of a
            // confusing "missing tag_name" parse failure.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateResult { NoReleasesPublished = true };

            if (!response.IsSuccessStatusCode)
                return new UpdateResult { ErrorMessage = $"GitHub API returned HTTP {(int)response.StatusCode}." };

            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement))
                return new UpdateResult { ErrorMessage = "Unexpected API response (missing tag_name)." };

            string tagName = tagElement.GetString() ?? string.Empty;
            string latestVersion = tagName.TrimStart('v', 'V');

            string releaseUrl = ReleasesUrl;
            if (doc.RootElement.TryGetProperty("html_url", out JsonElement urlElement))
                releaseUrl = urlElement.GetString() ?? ReleasesUrl;

            bool isNewer = IsVersionNewer(latestVersion, currentVersion);
            return new UpdateResult
            {
                UpdateAvailable = isNewer,
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl
            };
        }
        catch (HttpRequestException ex)
        {
            return new UpdateResult { ErrorMessage = $"Network error: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new UpdateResult { ErrorMessage = "Request timed out." };
        }
        catch (Exception ex)
        {
            return new UpdateResult { ErrorMessage = ex.Message };
        }
    }

    // ── Version comparison ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="latest"/> is strictly newer than
    /// <paramref name="current"/>.  Parses dotted numeric strings (e.g. "2.39");
    /// falls back to ordinal string comparison when parsing fails.
    /// </summary>
    internal static bool IsVersionNewer(string latest, string current)
    {
        if (TryParseVersion(latest, out Version? lv) && TryParseVersion(current, out Version? cv))
            return lv! > cv!;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    internal static bool TryParseVersion(string s, out Version? version)
    {
        // Version.TryParse requires at least two numeric parts ("2.39" is fine; "2" needs padding)
        if (!s.Contains('.'))
            s += ".0";
        return Version.TryParse(s, out version);
    }
}
