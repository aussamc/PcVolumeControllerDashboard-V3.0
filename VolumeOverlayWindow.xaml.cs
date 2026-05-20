using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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

    /// <summary>Update content and (re-)show the overlay.</summary>
    public void ShowOverlay(string channelName, int volumePercent, double timeoutSeconds, bool isDark)
    {
        // Update content
        ChannelNameText.Text = channelName;
        VolumeProgressBar.Value = volumePercent;
        VolumePercentText.Text = $"{volumePercent}%";

        // Theme colours
        if (isDark)
        {
            ContentBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
            ChannelNameText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
            VolumePercentText.Foreground = System.Windows.Media.Brushes.White;
            VolumeProgressBar.Foreground = System.Windows.Media.Brushes.White;
            VolumeProgressBar.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            ContentBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            ChannelNameText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x66, 0x66, 0x66));
            VolumePercentText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x11, 0x11, 0x11));
            VolumeProgressBar.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0x66, 0xCC));
            VolumeProgressBar.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x44, 0x00, 0x00, 0x00));
        }

        // Stop any existing hide timer
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 10.0));

        if (!_isShowing)
        {
            // Make visible at opacity 0, then animate in
            Opacity = 0;
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
            // Already visible — cancel any outgoing fade and restore full opacity
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }

        _hideTimer.Start();
    }

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
