using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>An available-update result surfaced to the UI (banner / notification).</summary>
public sealed class UpdateAvailableInfo
{
    public string LatestVersion { get; init; } = string.Empty;
    public string ReleaseUrl { get; init; } = UpdateCheckResult.ReleasesPage;

    /// <summary>Release download assets, for the download-and-apply engine (v3.19 part 2).</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();
}

/// <summary>
/// v3.19 auto-update checker automation. On launch (throttled) and on a periodic timer
/// it runs <see cref="UpdateCheckService"/> in the background, gated on the user's
/// <c>AutoCheckForUpdates</c> setting and suppressed under <c>--safe</c>. When a strictly
/// newer, non-skipped release is found it raises <see cref="UpdateAvailable"/> (for the
/// in-window banner) and fires a desktop notification, and remembers the result in
/// <see cref="Pending"/> so a window built after the check still sees it.
///
/// This is the "checker automation" slice of v3.19; the download/apply engine is a later
/// slice, so today the prompt's action opens the release page. Decision logic (throttle
/// + skip de-dup) lives in the pure Core <see cref="UpdatePolicy"/>; this class only owns
/// the network call, timer, and settings persistence.
/// </summary>
public sealed class UpdateOrchestrator : IDisposable
{
    // Don't re-query GitHub on every rapid restart, but check on a fresh launch after a
    // few idle hours. Periodic ticks pass no throttle (TimeSpan.Zero) so a long-running
    // session still picks up a release published mid-session.
    private static readonly TimeSpan LaunchThrottle = TimeSpan.FromHours(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    private readonly UpdateCheckService _updater;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly StartupOptions _startup;
    private readonly LogService _log;
    private readonly string _currentVersion = AppInfo.Version;

    private Timer? _timer;
    private int _checking; // 0/1 CAS guard so overlapping ticks don't double-query

    public UpdateOrchestrator(UpdateCheckService updater, SettingsService settings,
        NotificationService notifications, StartupOptions startup, LogService log)
    {
        _updater = updater;
        _settings = settings;
        _notifications = notifications;
        _startup = startup;
        _log = log;
    }

    /// <summary>The most recent promptable update, or null if none found yet / dismissed.</summary>
    public UpdateAvailableInfo? Pending { get; private set; }

    /// <summary>Raised on the UI thread when a promptable update is found.</summary>
    public event Action<UpdateAvailableInfo>? UpdateAvailable;

    /// <summary>
    /// Starts the throttled launch check and the periodic timer. No-op under <c>--safe</c>.
    /// Call once at startup (after the DI container is built).
    /// </summary>
    public void Start()
    {
        if (_startup.SafeMode)
        {
            _log.Log("Auto-update check suppressed (--safe).");
            return;
        }

        // The timer runs regardless of the current setting; each tick re-reads
        // AutoCheckForUpdates via UpdatePolicy, so toggling it on mid-session just works.
        _timer = new Timer(_ => _ = RunCheckAsync(launch: false), null, PollInterval, PollInterval);
        _ = RunCheckAsync(launch: true);
    }

    /// <summary>Manual "Check now": bypasses the throttle and the enabled gate.</summary>
    public Task CheckNowAsync() => RunCheckAsync(launch: false, force: true);

    private async Task RunCheckAsync(bool launch, bool force = false)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            return; // a check is already in flight

        try
        {
            DashboardSettings s = _settings.Settings;
            TimeSpan throttle = launch ? LaunchThrottle : TimeSpan.Zero;
            if (!force && !UpdatePolicy.ShouldAutoCheck(s.AutoCheckForUpdates, _startup.SafeMode,
                    s.LastUpdateCheckUtc, DateTime.UtcNow, throttle))
                return;

            UpdateCheckResult result = await _updater.CheckAsync(_currentVersion).ConfigureAwait(false);

            if (result.ErrorMessage != null)
            {
                // Don't stamp LastUpdateCheckUtc on failure, so a transient network error
                // doesn't push the next attempt a whole throttle window away.
                _log.Log($"Auto-update check failed: {result.ErrorMessage}");
                return;
            }

            // Settings persistence + event raise happen on the UI thread: the banner
            // subscriber lives there, and it serialises with any concurrent Save().
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                s.LastUpdateCheckUtc = DateTime.UtcNow;
                _settings.Save();

                if (!UpdatePolicy.ShouldPrompt(result.UpdateAvailable, result.LatestVersion, s.SkippedUpdateVersion))
                    return;

                var info = new UpdateAvailableInfo
                {
                    LatestVersion = result.LatestVersion,
                    ReleaseUrl = result.ReleaseUrl,
                    Assets = result.Assets,
                };
                Pending = info;
                _log.Log($"Update available: version {result.LatestVersion} (you have {_currentVersion}).");
                _notifications.NotifyUpdateAvailable(result.LatestVersion);
                UpdateAvailable?.Invoke(info);
            });
        }
        catch (Exception ex)
        {
            _log.Log($"Auto-update check error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// Persists a "skip this version" choice so it stops prompting until a strictly newer
    /// release appears, and clears the in-memory pending prompt.
    /// </summary>
    public void SkipVersion(string version)
    {
        _settings.Settings.SkippedUpdateVersion = version;
        _settings.Save();
        Pending = null;
        _log.Log($"Update {version} skipped by user.");
    }

    /// <summary>Clears the pending prompt for this session ("remind me later").</summary>
    public void DismissPending() => Pending = null;

    public void Dispose() => _timer?.Dispose();
}
