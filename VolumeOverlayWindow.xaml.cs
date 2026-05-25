using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;

namespace PcVolumeControllerDashboard;

public partial class VolumeOverlayWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private bool _isShowing;

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += HideTimer_Tick;
    }

    // ── Volume overlay ────────────────────────────────────────────────────────

    /// <summary>
    /// Show (or refresh) the overlay in volume mode — progress bar + percentage.
    /// Pass <paramref name="volumePercent"/> = −1 to show the channel name only.
    /// </summary>
    public void ShowOverlay(string channelName, int volumePercent, double timeoutSeconds, bool isDark)
    {
        // Hide the mute row; show the volume row.
        MuteRow.Visibility   = Visibility.Collapsed;
        VolumeRow.Visibility = Visibility.Visible;

        // Update content.
        ChannelNameText.Text  = channelName;
        ChannelNameText.FontSize = 12;

        bool showVolume = volumePercent >= 0;
        VolumeProgressBar.Visibility  = showVolume ? Visibility.Visible  : Visibility.Collapsed;
        VolumePercentText.Visibility  = showVolume ? Visibility.Visible  : Visibility.Collapsed;

        if (showVolume)
        {
            VolumeProgressBar.Value = volumePercent;
            VolumePercentText.Text  = $"{volumePercent}%";
        }

        ApplyTheme(isDark);
        ShowAnimated(timeoutSeconds);
    }

    // ── Mute overlay ──────────────────────────────────────────────────────────

    /// <summary>
    /// Show (or refresh) the overlay in mute mode — speaker icon + Muted / Unmuted text.
    /// </summary>
    public void ShowMuteOverlay(string channelName, bool isMuted, double timeoutSeconds, bool isDark)
    {
        // Hide the volume row; show the mute row.
        VolumeRow.Visibility = Visibility.Collapsed;
        MuteRow.Visibility   = Visibility.Visible;

        // Channel name is slightly larger when there is no sub-row below it.
        ChannelNameText.Text     = channelName;
        ChannelNameText.FontSize = 12;

        // Toggle which icon is visible.
        UnmutedSpeakerPath.Visibility = isMuted ? Visibility.Collapsed : Visibility.Visible;
        MutedSpeakerPath.Visibility   = isMuted ? Visibility.Visible   : Visibility.Collapsed;

        MuteStatusText.Text = isMuted ? "Muted" : "Unmuted";

        ApplyTheme(isDark);
        ShowAnimated(timeoutSeconds);
    }

    // ── Shared theme + animation helpers ─────────────────────────────────────

    private void ApplyTheme(bool isDark)
    {
        if (isDark)
        {
            ContentBorder.Background = new SolidColorBrush(WpfColor.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));

            var textWhite   = new SolidColorBrush(WpfColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            var textDimmed  = new SolidColorBrush(WpfColor.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
            var trackFill   = new SolidColorBrush(WpfColor.FromArgb(0x44, 0xFF, 0xFF, 0xFF));

            ChannelNameText.Foreground  = textDimmed;
            VolumePercentText.Foreground = textWhite;
            VolumeProgressBar.Foreground = textWhite;
            VolumeProgressBar.Background = trackFill;
            MuteStatusText.Foreground   = textWhite;

            var iconStroke = textWhite;
            UnmutedSpeakerPath.Stroke = iconStroke;
            MutedSpeakerPath.Stroke   = iconStroke;
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(WpfColor.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

            var textDark   = new SolidColorBrush(WpfColor.FromArgb(0xFF, 0x11, 0x11, 0x11));
            var textMedium = new SolidColorBrush(WpfColor.FromArgb(0xFF, 0x66, 0x66, 0x66));
            var barFill    = new SolidColorBrush(WpfColor.FromArgb(0xFF, 0x00, 0x66, 0xCC));
            var trackFill  = new SolidColorBrush(WpfColor.FromArgb(0x44, 0x00, 0x00, 0x00));

            ChannelNameText.Foreground  = textMedium;
            VolumePercentText.Foreground = textDark;
            VolumeProgressBar.Foreground = barFill;
            VolumeProgressBar.Background = trackFill;
            MuteStatusText.Foreground   = textDark;

            var iconStroke = textDark;
            UnmutedSpeakerPath.Stroke = iconStroke;
            MutedSpeakerPath.Stroke   = iconStroke;
        }
    }

    private void ShowAnimated(double timeoutSeconds)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 10.0));

        if (!_isShowing)
        {
            Opacity    = 0;
            Visibility = Visibility.Visible;
            _isShowing = true;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            // Already visible — cancel any outgoing fade and restore full opacity.
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }

        _hideTimer.Start();
    }

    // ── Hide ──────────────────────────────────────────────────────────────────

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        FadeOut();
    }

    private void FadeOut()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            _isShowing = false;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Immediately hide without animation (e.g. on app close).</summary>
    public void HideImmediate()
    {
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Collapsed;
        _isShowing = false;
    }
}
