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
    }

    // ── HTTP client (static — reuse socket) ─────────────────────────────────────

    private static readonly HttpClient _httpClient;

    static UpdateChecker()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PcVolumeControllerDashboard/2.39");
        // GitHub API requires Accept header
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
            string json = await _httpClient.GetStringAsync(ApiUrl).ConfigureAwait(false);
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
    private static bool IsVersionNewer(string latest, string current)
    {
        if (TryParseVersion(latest, out Version? lv) && TryParseVersion(current, out Version? cv))
            return lv! > cv!;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static bool TryParseVersion(string s, out Version? version)
    {
        // Version.TryParse requires at least two numeric parts ("2.39" is fine; "2" needs padding)
        if (!s.Contains('.'))
            s += ".0";
        return Version.TryParse(s, out version);
    }
}
